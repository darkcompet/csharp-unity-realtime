namespace Tool.Compet.Realtime {
	using System;
	using System.Text;
	using System.Net.WebSockets;
	using System.Threading;
	using System.Threading.Tasks;

	// using Cysharp.Threading.Tasks;
	using Tool.Compet.MessagePack;

	using Tool.Compet.Core;
	using Tool.Compet.Log;
	using System.Runtime.Serialization.Formatters.Binary;
	using System.IO;
	using System.Collections.Generic;

	/// Socket (TCP vs UDP): https://en.wikipedia.org/wiki/Nagle%27s_algorithm
	/// TCP test for Unity client: https://gist.github.com/danielbierwirth/0636650b005834204cb19ef5ae6ccedb
	/// Raw socket impl server side: https://stackoverflow.com/questions/36526332/simple-socket-server-in-unity
	/// Unity websocket-based socketio: https://github.com/itisnajim/SocketIOUnity
	/// Unity client websocket: https://devblogs.microsoft.com/xamarin/developing-real-time-communication-apps-with-websocket/
	/// Tasking with ThreadPool in C#: https://stackoverflow.com/questions/7889746/creating-threads-task-factory-startnew-vs-new-thread
	/// Compare with Ktor websocket: https://ktor.io/docs/websocket.html#api-overview
	/// Websocket client: https://github.com/Marfusios/websocket-client

	/// MessagePack for SignalR: https://docs.microsoft.com/en-us/aspnet/core/signalr/messagepackhubprotocol?view=aspnetcore-6.0
	/// SignalR for client: https://www.nuget.org/packages/Microsoft.AspNetCore.SignalR.Client/6.0.1

	/// Call methods via attribute: https://stackoverflow.com/questions/46359351/how-to-call-methods-with-method-attributes

	/// [From NuGet PM] Super websocket: https://www.supersocket.net/
	public class DkRealtimeNetwork {
		/// Caller should provide it if authentication is
		/// required by server, for eg,. "Bearer your_access_token"
		public string? authorization;

		/// Communicator between server and client
		private ClientWebSocket socket;

		/// Socket url for realtime, for eg,. wss://darkcompet.com/gaming
		private string socketUrl;

		/// To avoid allocate new array when receive message from server.
		private ArraySegment<byte> inBuffer;

		/// Indicate caller has requested close realtime network.
		private bool disconnectRequested;

		public static bool inRoom;
		public static bool connected;

		private long lastSentTime;

		public Action OnConnected;

		public DkRealtimeNetwork(string socketUrl, int bufferSize = 1 << 12) {
			this.socket = new();
			this.socketUrl = socketUrl;
			this.inBuffer = new ArraySegment<byte>(new byte[bufferSize], 0, bufferSize);

			// var tcp = new System.Net.Sockets.TcpClient();
		}

		/// @param `accessToken`: For user-authentication if server required. Skip pass if does not need.
		public async Task ConnectAsync() {
			var socket = this.socket;

			// [Connect to server]
			// Url must be started with `wss` since server using `HTTPS`
			// For cancellation token, also see `CancellationTokenSource, CancellationTokenSource.CancelAfter()` for detail.
			var cancellationToken = CancellationToken.None;
			var authorization = this.authorization;
			if (authorization != null) {
				socket.Options.SetRequestHeader("Authorization", authorization);
			}
			await socket.ConnectAsync(
				new Uri(socketUrl),
				cancellationToken
			);
			// Mark as connected
			connected = true;
			this.OnConnected?.Invoke();
			if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"Connected to realtime server: {socketUrl}"); }

			// [Awake server]
			// After connected, server is waiting something from each client. why???
			await this.SendAsync();

			// [Listen server's events]
			// Start new long-running background task to listen events from server.
			// We have to loop interval to check/receive message from server even though server has sent it to us.
			// See: https://devblogs.microsoft.com/xamarin/developing-real-time-communication-apps-with-websocket/
			await Task.Factory.StartNew(async () => {
				while (!this.disconnectRequested) {
					// Wait and Read server's message
					await this.ReceiveAsync();
				}
			}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}

		int sendRpcCount;
		public async Task SendRPC(string methodName, params object[] data) {
			if (this.disconnectRequested) {
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "Skip SendRPC since disconnectRequested"); }
				return;
			}

			var socket = this.socket;

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (socket.State == WebSocketState.Open) {
				this.lastSentTime = DkUtils.CurrentUnixTimeInMillis();

				// var outBytes = this.Serialize(data);
				var outBytes = MessagePackSerializer.Serialize(new TestMessagePackObj());

				await socket.SendAsync(
					new ArraySegment<byte>(outBytes),
					WebSocketMessageType.Binary,
					true,
					CancellationToken.None
				);
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"{sendRpcCount}-SendRPC, length: {outBytes.Length}"); }
			}
			else {
				DkLogs.Warning(this, $"Ignored SendRPC while socket-state is NOT open, current state: {socket.State}");
			}
		}

		long sendCount;
		/// Run in background.
		/// bytes: Encoding.UTF8.GetBytes(DkJsons.Obj2Json(new UserProfileResponse()))
		/// Send obj: https://stackoverflow.com/questions/15012549/send-typed-objects-through-tcp-or-sockets
		public async Task SendAsync(object? msgPackObj = null) {
			if (this.disconnectRequested) {
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "Skip sending since disconnectRequested"); }
				return;
			}

			var socket = this.socket;

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (socket.State == WebSocketState.Open) {
				this.lastSentTime = DkUtils.CurrentUnixTimeInMillis();

				// var outBytes = Encoding.UTF8.GetBytes("client test message");
				var outBytes = MessagePackSerializer.Serialize(new TestMessagePackObj {
					FirstName = "dk111",
					LastName = "cm222",
					Age = 100,
				});

				await socket.SendAsync(
					new ArraySegment<byte>(outBytes),
					WebSocketMessageType.Text,
					true,
					CancellationToken.None
				);
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"{++sendCount}-Sent to realtime server async"); }
			}
			else {
				DkLogs.Warning(this, $"Ignored send while socket-state is NOT open, current state: {socket.State}");
			}
		}

		int receiveCount;
		/// Run in background.
		private async Task ReceiveAsync() {
			var socket = this.socket;
			var inBuffer = this.inBuffer;

			if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"Entered ReceiveAsync, socket.State: {socket.State}"); }

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (socket.State != WebSocketState.Open) {
				DkLogs.Warning(this, $"Ignored receive while socket-state is NOT open, current state: {socket.State}");
				return;
			}

			// Read server-message and fill full into the buffer.
			// If we wanna looping to read as chunks (XXX bytes), we can check with `serverResult.EndOfMessage`
			// to detect when reading message (line) get completed.
			var inResult = await socket.ReceiveAsync(
				inBuffer,
				CancellationToken.None
			);

			// Server closes the connection
			if (inResult.CloseStatus.HasValue) {
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "Skip read message since socket was closed"); }
				return;
			}

			// [Parse server's message]
			switch (inResult.MessageType) {
				case WebSocketMessageType.Text: {
					// dkopt: avoid re-allocate new array by impl new deserialization method
					var fromArr = inBuffer.Array;
					// var toArr = new byte[inResult.Count];
					// Array.Copy(fromArr, toArr, inResult.Count);
					// var result = MessagePackSerializer.Deserialize<T>(resultArr);

					var result = Encoding.UTF8.GetString(fromArr, 0, inResult.Count);
					DkLogs.Info(this, $"{++receiveCount}-Got text after {DkUtils.CurrentUnixTimeInMillis() - lastSentTime} millis, result: {result}");
					break;
				}
				case WebSocketMessageType.Binary: {
					// dkopt: avoid re-allocate new array by impl new deserialization method
					// To copy different array-types, consider use `Buffer.BlockCopy`.
					var fromArr = inBuffer.Array;

					// var result = this.Deserialize(fromArr, 0, inResult.Count);

					var toArr = new byte[inResult.Count];
					Array.Copy(fromArr, 0, toArr, 0, inResult.Count);
					var result = MessagePackSerializer.Deserialize<TestMessagePackObj>(toArr);


					DkLogs.Info(this, $"{++receiveCount}-Got binary after {DkUtils.CurrentUnixTimeInMillis() - lastSentTime} millis, inResult.Count: {inResult.Count}, result: ", result);
					break;
				}
				default: {
					if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"Unhandled inResult.MessageType: {inResult.MessageType}"); }
					break;
				}
			}
		}

		public async void OnDestroy() {
			var socket = this.socket;

			try {
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "Caller has requested disconnect"); }

				this.disconnectRequested = true;

				// [Close connection and Dispose socket]
				await socket.CloseAsync(
					WebSocketCloseStatus.NormalClosure,
					"OK",
					CancellationToken.None
				);
				socket.Dispose();

				// Mark as disconnected
				connected = false;

				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "Closed connection and Disposed socket"); }
			}
			catch (Exception e) {
				DkLogs.Warning(this, $"Error when dispose socket, error: {e.Message}");
			}
		}

		/// Convert obj to bytes.
		private byte[] Serialize(object obj) {
			if (obj == null) {
				return null;
			}

			// var objects = new object[] { 1, "aaa", new { Anything = 9999 } };
			return MessagePackSerializer.Serialize(obj).AlsoDk(bytes => {
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "serialized to: " + MessagePackSerializer.ConvertToJson(bytes)); };
			});

			// var formatter = new BinaryFormatter();
			// var stream = new MemoryStream();
			// formatter.Serialize(stream, obj);

			// return stream.ToArray();
		}

		/// Convert bytes to obj.
		private object Deserialize(byte[] bytes, int offset, int count) {
			if (bytes == null) {
				return null;
			}

			var dst = new byte[count];
			Array.Copy(bytes, offset, dst, 0, count);
			if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "deserialized to: " + MessagePackSerializer.ConvertToJson(dst)); }
			return MessagePackSerializer.Deserialize<object>(dst);

			// var stream = new MemoryStream();
			// var formatter = new BinaryFormatter();
			// stream.Write(bytes, offset, count);
			// stream.Seek(offset, SeekOrigin.Begin);

			// return formatter.Deserialize(stream);
		}
	}

	/// Extends `Attribute` to make this class is collectable via attribute-reflection.
	public class DkRPC : Attribute {
	}
}
