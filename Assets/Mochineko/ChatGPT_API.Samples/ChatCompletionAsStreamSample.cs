#nullable enable
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Mochineko.ChatGPT_API.Memory;
using UnityEngine;

namespace Mochineko.ChatGPT_API.Samples
{
    /// <summary>
    /// A sample component to complete chat as stream by ChatGPT API on Unity.
    /// </summary>
    public sealed class ChatCompletionAsStreamSample : MonoBehaviour
    {
        [SerializeField, TextArea]
        private string prompt = string.Empty;

        /// <summary>
        /// Message sent to ChatGPT API.
        /// </summary>
        [SerializeField, TextArea] private string message = string.Empty;

        /// <summary>
        /// Max number of chat memory of queue.
        /// </summary>
        [SerializeField] private int maxMemoryCount = 10;

        private ChatCompletionAPIConnection? connection;
        private IChatMemory? memory;

        private void Start()
        {
            // Get API key from environment variable.
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("OpenAI API key must be set.");
                return;
            }

            memory = new FiniteQueueChatMemory(maxMemoryCount);

            // Create instance of ChatGPTConnection with specifying chat model.
            connection = new ChatCompletionAPIConnection(
                apiKey,
                memory,
                prompt);
        }

        [ContextMenu(nameof(SendChatAsStream))]
        public void SendChatAsStream()
        {
            SendChatAsStreamAsync(this.GetCancellationTokenOnDestroy())
                .Forget();
        }
        
        [ContextMenu(nameof(ClearChatMemory))]
        public void ClearChatMemory()
        {
            memory?.ClearAllMessages();
        }
        
        private async UniTask SendChatAsStreamAsync(CancellationToken cancellationToken)
        {
            // Validations
            if (connection == null)
            {
                Debug.LogError($"[ChatGPT_API.Samples] Connection is null.");
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                Debug.LogError($"[ChatGPT_API.Samples] Chat content is empty.");
                return;
            }

            var builder = new StringBuilder();
            try
            {
                await UniTask.SwitchToThreadPool();
                
                // Receive enumerable from ChatGPT chat completion API.
                var enumerable = await connection.CompleteChatAsStreamAsync(
                    message,
                    cancellationToken,
                    verbose: true);

                await foreach (var chunk in enumerable.WithCancellation(cancellationToken))
                {
                    // First response contains only "role" element.
                    if (chunk.Choices[0].Delta.Content is null)
                    {
                        continue;
                    }
                    
                    var delta = chunk.Choices[0].Delta.Content;
                    builder.Append(delta);
                    // Log chat delta.
                    Debug.Log($"[ChatGPT_API.Samples] Delta:{delta}, Total:{builder}");
                }
                
                // Log chat completion result.
                Debug.Log($"[ChatGPT_API.Samples] Total result:\n{builder}");
            }
            catch (Exception e)
            {
                // Exceptions should be caught.
                Debug.LogException(e);
                return;
            }
        }
    }
}