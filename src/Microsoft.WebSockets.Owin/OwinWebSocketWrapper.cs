﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.WebSockets.Owin
{
    using WebSocketSendAsync =
           Func
           <
               ArraySegment<byte> /* data */,
               int /* messageType */,
               bool /* endOfMessage */,
               CancellationToken /* cancel */,
               Task
           >;

    using WebSocketReceiveAsync =
        Func
        <
            ArraySegment<byte> /* data */,
            CancellationToken /* cancel */,
            Task
            <
                Tuple
                <
                    int /* messageType */,
                    bool /* endOfMessage */,
                    int? /* count */,
                    int? /* closeStatus */,
                    string /* closeStatusDescription */
                >
            >
        >;

    using WebSocketReceiveTuple =
        Tuple
        <
            int /* messageType */,
            bool /* endOfMessage */,
            int? /* count */,
            int? /* closeStatus */,
            string /* closeStatusDescription */
        >;

    using WebSocketCloseAsync =
        Func
        <
            int /* closeStatus */,
            string /* closeDescription */,
            CancellationToken /* cancel */,
            Task
        >;

    public class OwinWebSocketWrapper
    {
        private WebSocketContext context;
        private WebSocket webSocket;
        private IDictionary<string, object> environment;

        public OwinWebSocketWrapper(WebSocketContext context)
        {
            this.context = context;
            this.webSocket = context.WebSocket;

            environment = new Dictionary<string, object>();
            environment["websocket.SendAsyncFunc"] = new WebSocketSendAsync(SendAsync);
            environment["websocket.ReceiveAsyncFunc"] = new WebSocketReceiveAsync(ReceiveAsync);
            environment["websocket.CloseAsyncFunc"] = new WebSocketCloseAsync(CloseAsync);
            environment["websocket.CallCancelled"] = CancellationToken.None; // TODO:
            environment["websocket.Version"] = "1.0";
            
            environment[typeof(WebSocketContext).FullName] = context;
        }

        public IDictionary<string, object> Environment
        {
            get { return environment; }
        }

        public Task SendAsync(ArraySegment<byte> buffer, int messageType, bool endOfMessage, CancellationToken cancel)
        {
            // Remap close messages to CloseAsync.  System.Net.WebSockets.WebSocket.SendAsync does not allow close messages.
            if (messageType == 0x8)
            {
                return RedirectSendToCloseAsync(buffer, cancel);
            }

            return this.webSocket.SendAsync(buffer, OpCodeToEnum(messageType), endOfMessage, cancel);
        }

        public async Task<WebSocketReceiveTuple> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancel)
        {
            WebSocketReceiveResult nativeResult = await this.webSocket.ReceiveAsync(buffer, cancel);
            return new WebSocketReceiveTuple(
                EnumToOpCode(nativeResult.MessageType),
                nativeResult.EndOfMessage,
                (nativeResult.MessageType == WebSocketMessageType.Close ? null : (int?)nativeResult.Count),
                (int?)nativeResult.CloseStatus,
                nativeResult.CloseStatusDescription
                );
        }

        public Task CloseAsync(int status, string description, CancellationToken cancel)
        {
            return this.webSocket.CloseOutputAsync((WebSocketCloseStatus)status, description, cancel);
        }

        private Task RedirectSendToCloseAsync(ArraySegment<byte> buffer, CancellationToken cancel)
        {
            if (buffer.Array == null || buffer.Count == 0)
            {
                return this.CloseAsync(1000, string.Empty, cancel);
            }
            else if (buffer.Count >= 2)
            {
                // Unpack the close message.
                int statusCode =
                    (buffer.Array[buffer.Offset] << 8)
                    | buffer.Array[buffer.Offset + 1];
                string description = Encoding.UTF8.GetString(buffer.Array, buffer.Offset + 2, buffer.Count - 2);

                return this.CloseAsync(statusCode, description, cancel);
            }
            else
            {
                throw new ArgumentOutOfRangeException("buffer");
            }
        }

        public async Task CleanupAsync()
        {
            switch (this.webSocket.State)
            {
                case WebSocketState.Closed: // Closed gracefully, no action needed. 
                case WebSocketState.Aborted: // Closed abortively, no action needed.                       
                    break;
                case WebSocketState.CloseReceived:
                    await this.webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        string.Empty, CancellationToken.None /*TODO:*/);
                    break;
                case WebSocketState.Open:
                case WebSocketState.CloseSent: // No close received, abort so we don't have to drain the pipe.
                    this.webSocket.Abort();
                    break;
                default:
                    throw new ArgumentOutOfRangeException("state", this.webSocket.State, string.Empty);
            }
        }

        private WebSocketMessageType OpCodeToEnum(int messageType)
        {
            switch (messageType)
            {
                case 0x1: return WebSocketMessageType.Text;
                case 0x2: return WebSocketMessageType.Binary;
                case 0x8: return WebSocketMessageType.Close;
                default:
                    throw new ArgumentOutOfRangeException("messageType", messageType, string.Empty);
            }
        }

        private int EnumToOpCode(WebSocketMessageType webSocketMessageType)
        {
            switch (webSocketMessageType)
            {
                case WebSocketMessageType.Text: return 0x1;
                case WebSocketMessageType.Binary: return 0x2;
                case WebSocketMessageType.Close: return 0x8;
                default:
                    throw new ArgumentOutOfRangeException("webSocketMessageType", webSocketMessageType, string.Empty);
            }
        }
    }
}