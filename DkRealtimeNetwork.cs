namespace Tool.Compet.Realtime {
	using System;
	using System.Net.WebSockets;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Cysharp.Threading.Tasks;
	using MessagePack;

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
		/// Caller should provide it if authentication is required by server.
		/// For eg,. "Bearer your_access_token"
		public string? authorization;

		public static bool inRoom;
		public static bool connected;

		/// Realtime handler (message sender, receiver)
		private ClientWebSocket socket;
		/// Socket realtime network url (for eg,. wss://darkcompet.com/gaming)
		private string socketUrl;

		/// Buffering message between server/client
		private byte[] buffer;

		private long lastSentTime;

		public DkRealtimeNetwork(string socketUrl, int bufferSize = 1 << 10) {
			this.socket = new();
			this.socketUrl = socketUrl;
			this.buffer = new byte[bufferSize];

			// var tcp = new System.Net.Sockets.TcpClient();
		}

		/// @param `accessToken`: For user-authentication if server required. Skip pass if does not need.
		public async Task ConnectAsync() {
			var socket = this.socket;

			// [Connect to server via socket]
			// Url must be started with `wss` since server using `HTTPS`
			// For cancellation token, also see `CancellationTokenSource, CancellationTokenSource.CancelAfter()` for detail.
			var cancellationToken = CancellationToken.None;
			if (this.authorization != null) {
				socket.Options.SetRequestHeader("Authorization", this.authorization);
			}
			await socket.ConnectAsync(
				new System.Uri(socketUrl),
				cancellationToken
			);
			if (BuildConfig.DEBUG) { AppLogs.Debug(this, $"Connected to realtime server: {socketUrl}"); }

			// [Awake server]
			// After connected, server is waiting something from each client. why???
			await this.SendAsync();

			// [Listen server's events]
			// Start new long-running background task to listen events from server.
			// We have to loop interval to check/receive message from server even though server has sent it to us.
			// See: https://devblogs.microsoft.com/xamarin/developing-real-time-communication-apps-with-websocket/
			await Task.Factory.StartNew(async () => {
				while (true) {
					if (BuildConfig.DEBUG) { AppLogs.Debug(this, "Receiveing data from server..."); }
					await ReceiveAsync<TestMessagePackObj>();
				}
			}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}

		/// bytes: Encoding.UTF8.GetBytes(DkJsons.Obj2Json(new UserProfileResponse()))
		public async UniTask SendAsync(object? msgPackObj = null) {
			var webSocket = this.socket;

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (webSocket.State == WebSocketState.Open) {
				this.lastSentTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

				var bytes = Encoding.UTF8.GetBytes("MessagePackSerializer.Serialize(msgPackObj);");

				await webSocket.SendAsync(
					new ArraySegment<byte>(bytes),
					WebSocketMessageType.Text,
					true,
					CancellationToken.None
				);
				AppLogs.Debug(this, "Sent to realtime server async");
			}
			else {
				AppLogs.Warning(this, "Ignored send while socket-state is NOT open, current state: " + webSocket.State);
			}
		}

		public async UniTask ReceiveAsync<T>() where T : class {
			var socket = this.socket;
			var buffer = this.buffer;

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (socket.State == WebSocketState.Open) {
				// Read server-message and fill full into the buffer.
				// If we wanna looping to read as chunks (XXX bytes), we can check with `serverResult.EndOfMessage`
				// to detect when reading message (line) get completed.
				var serverResult = await socket.ReceiveAsync(
					new ArraySegment<byte>(buffer, 0, buffer.Length),
					CancellationToken.None
				);

				// Server closes the connection
				if (serverResult.CloseStatus.HasValue) {
					AppLogs.Debug(this, "Websocket connection has closed");
				}
				else {
					// dkopt: avoid re-allocate new array by impl new deserialization method
					// To copy different array-types, consider use `Buffer.BlockCopy`.
					var resultArr = new byte[serverResult.Count];
					Array.Copy(buffer, resultArr, serverResult.Count);
					// var result = MessagePackSerializer.Deserialize<T>(resultArr);
					var result = Encoding.UTF8.GetString(buffer, 0, serverResult.Count);

					AppLogs.Debug(this, $"Got result after {DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - lastSentTime} millis, result: {result}");
				}
			}
			else {
				AppLogs.Warning(this, "Ignored receive while socket-state is NOT open, current state: " + socket.State);
			}
		}

		public async void OnDestroy() {
			var socket = this.socket;

			await socket.CloseAsync(
				WebSocketCloseStatus.NormalClosure,
				"OK",
				CancellationToken.None
			);

			socket.Dispose();

			if (BuildConfig.DEBUG) { AppLogs.Debug(this, "Closed and Disposed socket"); }
		}
	}

	[MessagePackObject]
	public class TestMessagePackObj {
		// Key attributes take a serialization index (or string name)
		// The values must be unique and versioning has to be considered as well.
		// Keys are described in later sections in more detail.
		[Key(0)]
		public int Age { get; set; } = 100;

		[Key(1)]
		public string FirstName { get; set; } = "dark";

		[Key(2)]
		public string LastName { get; set; } = "compet";

		// All fields or properties that should not be serialized must be annotated with [IgnoreMember].
		[IgnoreMember]
		public string FullName { get { return FirstName + LastName; } }
	}
}
