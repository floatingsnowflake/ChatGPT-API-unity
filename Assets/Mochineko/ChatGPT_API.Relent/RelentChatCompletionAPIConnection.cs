#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mochineko.Relent.Result;
using Mochineko.Relent.UncertainResult;

namespace Mochineko.ChatGPT_API.Relent
{
    /// <summary>
    /// Binds ChatGPT chat completion API with resilient error handling using Relent.
    /// </summary>
    public sealed class RelentChatCompletionAPIConnection
    {
        private readonly string apiKey;
        private readonly IChatMemory chatMemory;
        private readonly HttpClient httpClient;
        
        private const string ChatCompletionEndPoint = "https://api.openai.com/v1/chat/completions";

        /// <summary>
        /// Create an instance of ChatGPT chat completion API connection.
        /// https://platform.openai.com/docs/api-reference/chat/create
        /// </summary>
        /// <param name="apiKey">API key generated by OpenAI</param>
        /// <param name="chatMemory"></param>
        /// <param name="prompt"></param>
        /// <param name="httpClient"></param>
        /// <exception cref="ArgumentNullException">API Key must be set</exception>
        public RelentChatCompletionAPIConnection(
            string apiKey,
            IChatMemory? chatMemory = null,
            string? prompt = null,
            HttpClient? httpClient = null)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentNullException(nameof(apiKey));
            }

            this.apiKey = apiKey;
            this.chatMemory = chatMemory ?? new SimpleChatMemory();

            if (!string.IsNullOrEmpty(prompt))
            {
                this.chatMemory.AddMessageAsync(
                    new Message(Role.System, prompt),
                    CancellationToken.None);
            }

            this.httpClient = httpClient ?? HttpClientPool.PooledClient;
        }

        /// <summary>
        /// Completes chat though ChatGPT chat completion API.
        /// </summary>
        /// <param name="content">Message content to send ChatGPT API</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="model"></param>
        /// <param name="functions"></param>
        /// <param name="functionCallString"></param>
        /// <param name="functionCallSpecifying"></param>
        /// <param name="temperature"></param>
        /// <param name="topP"></param>
        /// <param name="n"></param>
        /// <param name="stream"></param>
        /// <param name="stop"></param>
        /// <param name="maxTokens"></param>
        /// <param name="presencePenalty"></param>
        /// <param name="frequencyPenalty"></param>
        /// <param name="logitBias"></param>
        /// <param name="user"></param>
        /// <returns>Response from ChatGPT chat completion API.</returns>
        public async Task<IUncertainResult<ChatCompletionResponseBody>> CompleteChatAsync(
            string content,
            CancellationToken cancellationToken,
            Model model = Model.Turbo,
            IReadOnlyList<Function>? functions = null,
            string? functionCallString = null,
            FunctionCallSpecifying? functionCallSpecifying = null,
            float? temperature = null,
            float? topP = null,
            uint? n = null,
            bool? stream = null,
            string[]? stop = null,
            int? maxTokens = null,
            float? presencePenalty = null,
            float? frequencyPenalty = null,
            Dictionary<int, int>? logitBias = null,
            string? user = null)
        {
            if (string.IsNullOrEmpty(content))
            {
                return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                    "Failed because content is null or empty.");
            }
            
            if (cancellationToken.IsCancellationRequested)
            {
                return UncertainResults.RetryWithTrace<ChatCompletionResponseBody>(
                    "Retryable because cancellation has been already requested.");
            }

            // Record user message
            try
            {
                await chatMemory.AddMessageAsync(
                    new Message(Role.User, content),
                    cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                return UncertainResults.RetryWithTrace<ChatCompletionResponseBody>(
                    $"Retryable because operation has been canceled during recording user message -> {exception.Message}.");
            }

            // Create request body
            var requestBody = new ChatCompletionRequestBody(
                model.ToText(),
                chatMemory.Messages,
                functions,
                functionCallString,
                functionCallSpecifying,
                temperature,
                topP,
                n,
                stream,
                stop,
                maxTokens,
                presencePenalty,
                frequencyPenalty,
                logitBias,
                user);

            // Serialize request body
            string requestBodyJson; 
            var serializationResult = requestBody.SerializeRequestBody();
            if (serializationResult is ISuccessResult<string> serializationSuccess)
            {
                requestBodyJson = serializationSuccess.Result;
            }
            else if (serializationResult is IFailureResult<string> serializationFailure)
            {
                return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                    $"Failed because -> {serializationFailure.Message}.");
            }
            else
            {
                throw new ResultPatternMatchException(nameof(serializationResult));
            }

            // Build request message
            using var requestMessage = new HttpRequestMessage(
                HttpMethod.Post, 
                ChatCompletionEndPoint);

            // Request headers
            requestMessage
                .Headers
                .Add("Authorization", $"Bearer {apiKey}");

            // Request contents
            var requestContent = new StringContent(
                content: requestBodyJson,
                encoding: System.Text.Encoding.UTF8,
                mediaType: "application/json");

            requestMessage.Content = requestContent;

            try
            {
                // Post request and receive response
                using var responseMessage = await httpClient
                    .SendAsync(requestMessage, cancellationToken);
                
                if (responseMessage == null)
                {
                    return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                        $"Failed because {nameof(HttpResponseMessage)} was null.");
                }

                if (responseMessage.Content == null)
                {
                    return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                        $"Failed because {nameof(HttpResponseMessage.Content)} was null.");
                }
                
                // Succeeded
                if (responseMessage.IsSuccessStatusCode)
                {
                    var responseJson = await responseMessage.Content.ReadAsStringAsync();
                    if (string.IsNullOrEmpty(responseJson))
                    {
                        return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                            $"Failed because response string was null.");
                    }
                    
                    // Deserialize response body
                    ChatCompletionResponseBody responseBody;
                    var deserializationResult = JsonSerializer.DeserializeResponseBody(responseJson);
                    if (deserializationResult is ISuccessResult<ChatCompletionResponseBody> deserializationSuccess)
                    {
                        responseBody = deserializationSuccess.Result;
                    }
                    else if (deserializationResult is IFailureResult<ChatCompletionResponseBody> deserializationFailure)
                    {
                        return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                            $"Failed because JSON deserialization of {nameof(ChatCompletionResponseBody)} was failure -> {deserializationFailure.Message}.");
                    }
                    else
                    {
                        throw new ResultPatternMatchException(nameof(deserializationResult));
                    }

                    if (responseBody.Choices.Length == 0)
                    {
                        return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                            $"Failed because {nameof(ChatCompletionResponseBody.Choices)} was empty.");
                    }

                    // Record assistant messages
                    foreach (var choice in responseBody.Choices)
                    {
                        await chatMemory.AddMessageAsync(
                            choice.Message,
                            cancellationToken);
                    }

                    return UncertainResults.Succeed(responseBody);
                }
                // Retryable
                else if (responseMessage.StatusCode is HttpStatusCode.TooManyRequests
                         || (int)responseMessage.StatusCode is >= 500 and <= 599)
                {
                    return UncertainResults.RetryWithTrace<ChatCompletionResponseBody>(
                        $"Retryable because the API returned status code:({(int)responseMessage.StatusCode}){responseMessage.StatusCode}.");
                }
                // Response error
                else
                {
                    return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                        $"Failed because the API returned status code:({(int)responseMessage.StatusCode}){responseMessage.StatusCode}."
                    );
                }
            }
            // Request error
            catch (HttpRequestException exception)
            {
                return UncertainResults.RetryWithTrace<ChatCompletionResponseBody>(
                    $"Retryable because {nameof(HttpRequestException)} was thrown during calling the API -> {exception}.");
            }
            // Task cancellation
            catch (TaskCanceledException exception)
                when (exception.CancellationToken == cancellationToken)
            {
                return UncertainResults.RetryWithTrace<ChatCompletionResponseBody>(
                    $"Failed because task was canceled by user during call to the API -> {exception}.");
            }
            // Operation cancellation 
            catch (OperationCanceledException exception)
            {
                return UncertainResults.RetryWithTrace<ChatCompletionResponseBody>(
                    $"Retryable because operation was cancelled during calling the API -> {exception}.");
            }
            // Unhandled error
            catch (Exception exception)
            {
                return UncertainResults.FailWithTrace<ChatCompletionResponseBody>(
                    $"Failed because an unhandled exception was thrown when calling the API -> {exception}.");
            }
        }
        
        /// <summary>
        /// Completes chat though ChatGPT chat completion API.
        /// </summary>
        /// <param name="content">Message content to send ChatGPT API</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="model"></param>
        /// <param name="functions"></param>
        /// <param name="functionCallString"></param>
        /// <param name="functionCallSpecifying"></param>
        /// <param name="temperature"></param>
        /// <param name="topP"></param>
        /// <param name="n"></param>
        /// <param name="stream"></param>
        /// <param name="stop"></param>
        /// <param name="maxTokens"></param>
        /// <param name="presencePenalty"></param>
        /// <param name="frequencyPenalty"></param>
        /// <param name="logitBias"></param>
        /// <param name="user"></param>
        /// <returns>Response from ChatGPT chat completion API.</returns>
        public async Task<IUncertainResult<Stream>> CompleteChatAsStreamAsync(
            string content,
            CancellationToken cancellationToken,
            Model model = Model.Turbo,
            IReadOnlyList<Function>? functions = null,
            string? functionCallString = null,
            FunctionCallSpecifying? functionCallSpecifying = null,
            float? temperature = null,
            float? topP = null,
            uint? n = null,
            bool? stream = null,
            string[]? stop = null,
            int? maxTokens = null,
            float? presencePenalty = null,
            float? frequencyPenalty = null,
            Dictionary<int, int>? logitBias = null,
            string? user = null)
        {
            if (string.IsNullOrEmpty(content))
            {
                return UncertainResults.FailWithTrace<Stream>(
                    "Failed because content is null or empty.");
            }
            
            if (cancellationToken.IsCancellationRequested)
            {
                return UncertainResults.RetryWithTrace<Stream>(
                    "Retryable because cancellation has been already requested.");
            }

            // Record user message
            try
            {
                await chatMemory.AddMessageAsync(
                    new Message(Role.User, content),
                    cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                return UncertainResults.RetryWithTrace<Stream>(
                    $"Retryable because operation was cancelled during recording user message -> {exception}.");
            }

            // Create request body
            var requestBody = new ChatCompletionRequestBody(
                model.ToText(),
                chatMemory.Messages,
                functions,
                functionCallString,
                functionCallSpecifying,
                temperature,
                topP,
                n,
                stream,
                stop,
                maxTokens,
                presencePenalty,
                frequencyPenalty,
                logitBias,
                user);

            // Serialize request body
            string requestBodyJson; 
            var serializationResult = requestBody.SerializeRequestBody();
            if (serializationResult is ISuccessResult<string> serializationSuccess)
            {
                requestBodyJson = serializationSuccess.Result;
            }
            else if (serializationResult is IFailureResult<string> serializationFailure)
            {
                return UncertainResults.FailWithTrace<Stream>(
                    $"Failed because -> {serializationFailure.Message}.");
            }
            else
            {
                throw new ResultPatternMatchException(nameof(serializationResult));
            }

            // Build request message
            using var requestMessage = new HttpRequestMessage(
                HttpMethod.Post, 
                ChatCompletionEndPoint);

            // Request headers
            requestMessage
                .Headers
                .Add("Authorization", $"Bearer {apiKey}");

            // Request contents
            var requestContent = new StringContent(
                content: requestBodyJson,
                encoding: System.Text.Encoding.UTF8,
                mediaType: "application/json");

            requestMessage.Content = requestContent;

            try
            {
                // Post request and receive response
                using var responseMessage = await httpClient
                    .SendAsync(requestMessage, cancellationToken);
                
                if (responseMessage == null)
                {
                    return UncertainResults.FailWithTrace<Stream>(
                        $"Failed because {nameof(HttpResponseMessage)} was null.");
                }

                if (responseMessage.Content == null)
                {
                    return UncertainResults.FailWithTrace<Stream>(
                        $"Failed because {nameof(HttpResponseMessage.Content)} was null.");
                }

                // Succeeded
                if (responseMessage.IsSuccessStatusCode)
                {
                    var responseStream = await responseMessage.Content.ReadAsStreamAsync();

                    return UncertainResults.Succeed(responseStream);
                }
                // Retryable
                else if (responseMessage.StatusCode is HttpStatusCode.TooManyRequests
                         || (int)responseMessage.StatusCode is >= 500 and <= 599)
                {
                    return UncertainResults.RetryWithTrace<Stream>(
                        $"Retryable because the API returned status code:({(int)responseMessage.StatusCode}){responseMessage.StatusCode}.");
                }
                // Response error
                else
                {
                    return UncertainResults.FailWithTrace<Stream>(
                        $"Failed because the API returned status code:({(int)responseMessage.StatusCode}){responseMessage.StatusCode}."
                    );
                }
            }
            // Request error
            catch (HttpRequestException exception)
            {
                return UncertainResults.RetryWithTrace<Stream>(
                    $"Retryable because {nameof(HttpRequestException)} was thrown during calling the API -> {exception}.");
            }
            // Task cancellation
            catch (TaskCanceledException exception)
                when (exception.CancellationToken == cancellationToken)
            {
                return UncertainResults.RetryWithTrace<Stream>(
                    $"Failed because task was canceled by user during call to the API -> {exception}.");
            }
            // Operation cancellation 
            catch (OperationCanceledException exception)
            {
                return UncertainResults.RetryWithTrace<Stream>(
                    $"Retryable because operation was cancelled during calling the API -> {exception}.");
            }
            // Unhandled error
            catch (Exception exception)
            {
                return UncertainResults.FailWithTrace<Stream>(
                    $"Failed because an unhandled exception was thrown when calling the API -> {exception}.");
            }
        }
    }
}