using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

#pragma warning disable CA1031

namespace PokerBot
{
	internal static class Program
	{
		private static TelegramBotClient Bot;
		private static TelegramWrapper Telegram;
		private static User my;
		private static readonly Dictionary<long, Game> gamesByChatId = new Dictionary<long, Game>();
		internal static readonly Dictionary<int, Game> gamesByUserId = new Dictionary<int, Game>();

		private static Game GetOrMakeGame(Chat chat)
			=> gamesByChatId.TryGetValue(chat.Id, out var game) ? game : gamesByChatId[chat.Id] = new Game(Telegram, chat);
		private static Game GetGame(User user)
			=> gamesByUserId.TryGetValue(user.Id, out var game) ? game : null;
		internal static IEnumerable<int> Colors(this IEnumerable<Card> cards) => cards.Select(c => c.Color);

		private static async Task Main(string[] args)
		{
			Trace.Listeners.Add(new ConsoleListener());
			Trace.WriteLine("Starting");
			Bot = new TelegramBotClient(args[0]);
			Telegram = new TelegramWrapper(Bot);
			my = await Bot.GetMeAsync();
			Trace.WriteLine("Bot ready");
			Bot.OnReceiveError += (s, e) => Trace.TraceError($"ReceiveError {e.ApiRequestException}");
			Bot.OnReceiveGeneralError += (s, e) => Trace.TraceError($"ReceiveGeneralError {e.Exception}");
			Bot.OnMessage += (s, e) => _ = SafeDoTask(OnMessage, e.Message);
			Bot.OnInlineQuery += (s, e) => _ = SafeDoTask(OnInlineQuery, e.InlineQuery);
			Bot.OnCallbackQuery += (s, e) => _ = SafeDoTask(OnCallbackQuery, e.CallbackQuery);
			await Bot.SetMyCommandsAsync(new[] {
				new BotCommand { Command = "/start", Description = "Démarrer une partie" },
				new BotCommand { Command = "/b", Description = "Relancer d'un montant de votre choix" },
			});
			Bot.StartReceiving();
			for (; ;)
			{
				var command = Console.ReadLine().ToLower();
				Trace.WriteLine("SysRequest: " + command);
				if (command == "exit")
					break;
			}
			Trace.WriteLine("Exiting");
		}

		private static async Task SafeDoTask<T>(Func<T, Task> taskFunc, T e)
		{
			try
			{
				await taskFunc(e);
			}
			catch (Exception ex)
			{
				Trace.TraceError($"Exception in {taskFunc.Method.Name} : {ex}");
			}
		}

		private static async Task OnMessage(Message msg)
		{
			Console.WriteLine($"{msg.Chat.Title}>{msg.From.Username}> {msg.Text}");
			var text = msg.Text;
			if (string.IsNullOrEmpty(text)) return;
			if (text.StartsWith("/"))
			{
				var argsIndex = text.IndexOf(' ');
				var command = argsIndex == -1 ? text : text.Remove(argsIndex);
				string args = argsIndex == -1 ? "" : text.Substring(argsIndex + 1);
				argsIndex = command.LastIndexOf('@');
				if (argsIndex >= 0)
				{
					if (!command.Substring(argsIndex + 1).Equals(my.Username, StringComparison.OrdinalIgnoreCase))
						return; // command not for me => ignored
					command = command.Remove(argsIndex);
				}
				await GetOrMakeGame(msg.Chat).OnCommand(msg, command.ToLower(), args);
			}
		}

		private static async Task OnCallbackQuery(CallbackQuery query)
		{
			var words = query.Data.Split(' ');
			if (words.Length < 2 || !int.TryParse(words[1], out int chatId) || !gamesByChatId.TryGetValue(chatId, out var game))
			{
				await Bot.AnswerCallbackQueryAsync(query.Id, "Commande invalide");
				return;
			}
			await game.OnCallback(query, words);
		}

		private static async Task OnInlineQuery(InlineQuery query)
		{
			var game = GetGame(query.From);
			if (game == null)
				await Telegram.AnswerInline(query);
			else
				await game.OnInline(query);
		}
	}
}
