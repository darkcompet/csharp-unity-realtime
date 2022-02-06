namespace Tool.Compet.Realtime {
	using Tool.Compet.MessagePack;

	using System.Runtime.Serialization.Formatters.Binary;
	using System.IO;
	using System.Collections.Generic;

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
