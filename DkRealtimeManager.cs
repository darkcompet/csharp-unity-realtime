namespace Tool.Compet.Realtime {
	using System;
	
	public class DkRealtimeManager {
		public static readonly DkRealtimeManager instance = new();
	
		public void LeaveRoom(Action onCompleted = null) {
			onCompleted?.Invoke();
		}
	}
}
