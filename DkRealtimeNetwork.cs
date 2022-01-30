namespace Tool.Compet.Realtime {
	using System;
	using System.Text;
	using System.Net.WebSockets;
	using System.Threading;
	using System.Threading.Tasks;

	using Cysharp.Threading.Tasks;
	using MessagePack;

	using Tool.Compet.Core;
	using Tool.Compet.Log;

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
		private string realtimeSocketUrl;

		/// To avoid allocate new array when receive message from server.
		private ArraySegment<byte> inBuffer;

		/// Indicate caller has requested close realtime network.
		private bool disconnectRequested;

		public static bool inRoom;
		public static bool connected;

		private long lastSentTime;
		long testCounter;

		public DkRealtimeNetwork(string networkSocketUrl, int bufferSize = 1 << 10) {
			this.socket = new();
			this.realtimeSocketUrl = networkSocketUrl;
			this.inBuffer = new ArraySegment<byte>(new byte[bufferSize], 0, bufferSize);

			// var tcp = new System.Net.Sockets.TcpClient();
		}

		/// @param `accessToken`: For user-authentication if server required. Skip pass if does not need.
		public async UniTask ConnectAsync() {
			var socket = this.socket;

			// [Connect to server]
			// Url must be started with `wss` since server using `HTTPS`
			// For cancellation token, also see `CancellationTokenSource, CancellationTokenSource.CancelAfter()` for detail.
			var cancellationToken = CancellationToken.None;
			if (this.authorization != null) {
				socket.Options.SetRequestHeader("Authorization", this.authorization);
			}
			await socket.ConnectAsync(
				new Uri(realtimeSocketUrl),
				cancellationToken
			);
			// Mark as connected
			connected = true;
			if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"Connected to realtime server: {realtimeSocketUrl}"); }

			// [Awake server]
			// After connected, server is waiting something from each client. why???
			await this.SendAsync();

			// [Listen server's events]
			// Start new long-running background task to listen events from server.
			// We have to loop interval to check/receive message from server even though server has sent it to us.
			// See: https://devblogs.microsoft.com/xamarin/developing-real-time-communication-apps-with-websocket/
			await Task.Factory.StartNew(async () => {
				while (!this.disconnectRequested) {
					if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"Receiveing-{++testCounter} data from server..."); }
					// If we don't wait (use await) receive-method, this loop
					// will be run immediately even though receive-method is not completed.
					// => so we should await receive-method to make current thread wait reading server's event.
					await ReceiveAsync<TestMessagePackObj>();
				}
			}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		}

		/// Run in background.
		/// bytes: Encoding.UTF8.GetBytes(DkJsons.Obj2Json(new UserProfileResponse()))
		public async UniTask SendAsync(object? msgPackObj = null) {
			if (this.disconnectRequested) {
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, "Skip sending since disconnectRequested"); }
				return;
			}

			var socket = this.socket;

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (socket.State == WebSocketState.Open) {
				this.lastSentTime = DkUtils.CurrentUnixTimeInMillis();

				var outBytes = Encoding.UTF8.GetBytes("MessagePackSerializer.Serialize(msgPackObj);");

				await socket.SendAsync(
					new ArraySegment<byte>(outBytes),
					WebSocketMessageType.Text,
					true,
					CancellationToken.None
				);
				if (DkBuildConfig.DEBUG) { DkLogs.Debug(this, $"Sent-{++testCounter} to realtime server async"); }
			}
			else {
				DkLogs.Warning(this, $"Ignored send while socket-state is NOT open, current state: {socket.State}");
			}
		}

		/// Run in background.
		private async UniTask ReceiveAsync<T>() where T : class {
			var socket = this.socket;
			var inBuffer = this.inBuffer;

			// Action `send` must be performed while socket connection is open.
			// Otherwise we get exception.
			if (socket.State == WebSocketState.Open) {
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
				}
				else {
					// dkopt: avoid re-allocate new array by impl new deserialization method
					// To copy different array-types, consider use `Buffer.BlockCopy`.
					var fromArr = inBuffer.Array;
					// var toArr = new byte[inResult.Count];
					// Array.Copy(fromArr, toArr, inResult.Count);
					// var result = MessagePackSerializer.Deserialize<T>(resultArr);
					var result = Encoding.UTF8.GetString(fromArr, 0, inResult.Count);

					DkLogs.Info(this, $"Got result after {DkUtils.CurrentUnixTimeInMillis() - lastSentTime} millis, result: {result}");
				}
			}
			else {
				DkLogs.Warning(this, "Ignored receive while socket-state is NOT open, current state: " + socket.State);
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
