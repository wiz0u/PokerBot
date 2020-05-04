using System;
using System.Diagnostics;

namespace PokerBot
{
	internal class ConsoleListener : TraceListener
	{
		private ConsoleColor color = ConsoleColor.White;

		public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id)
		{
			SetColor(eventType);
			base.TraceEvent(eventCache, source, eventType, id);
			SetColor();
		}
		public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string format, params object[] args)
		{
			SetColor(eventType);
			base.TraceEvent(eventCache, source, eventType, id, format, args);
			SetColor();
		}
		public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id, string message)
		{
			SetColor(eventType);
			base.TraceEvent(eventCache, source, eventType, id, message);
			SetColor();
		}

		private void SetColor(TraceEventType eventType = 0)
		{
			color = eventType switch
			{
				TraceEventType.Critical => ConsoleColor.Magenta,
				TraceEventType.Error => ConsoleColor.Red,
				TraceEventType.Warning => ConsoleColor.Yellow,
				TraceEventType.Information => ConsoleColor.Cyan,
				_ => ConsoleColor.White,
			};
		}

		public override void Write(string message)
		{
			var save = Console.ForegroundColor;
			Console.ForegroundColor = color;
			Console.Write(message);
			Console.ForegroundColor = save;
		}
		public override void WriteLine(string message)
		{
			var save = Console.ForegroundColor;
			Console.ForegroundColor = color;
			Console.WriteLine(message);
			Console.ForegroundColor = save;
		}
	}
}