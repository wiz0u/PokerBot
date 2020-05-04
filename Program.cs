using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

#pragma warning disable CA1031

namespace PokerBot
{
	internal static class Program
	{
		private static TelegramBotClient Bot;
		private static readonly HashSet<User> knownUsers = new HashSet<User>();
		public static User my;

		#region Infrastructure
		private static async Task Main(string[] args)
		{
			Trace.Listeners.Add(new ConsoleListener());
			Trace.WriteLine("Starting");
			Bot = new TelegramBotClient(args[0]);
			my = await Bot.GetMeAsync();
			CheckUser(my);
			Trace.WriteLine("Bot ready");
			Bot.OnReceiveError += (s, e) => Trace.TraceError($"ReceiveError {e.ApiRequestException}");
			Bot.OnReceiveGeneralError += (s, e) => Trace.TraceError($"ReceiveGeneralError {e.Exception}");
			Bot.OnMessage += (s, e) => _ = DoTask(OnMessage, e.Message);
			Bot.OnInlineQuery += (s, e) => _ = DoTask(OnInlineQuery, e.InlineQuery);
			Bot.OnCallbackQuery += (s, e) => _ = DoTask(OnCallbackQuery, e.CallbackQuery);
			await Bot.SetMyCommandsAsync(new[] {
				new BotCommand { Command = "/start", Description = "Démarrer une partie" },
				new BotCommand { Command = "/relance", Description = "Relancer d'un montant de votre choix" },
			});
			Bot.StartReceiving();
			for (; ;)
			{
				var command = Console.ReadLine().ToLower();
				Trace.WriteLine("SysRequest: " + command);
				if (command == "exit")
					break;
				else if (command == "users")
				{
					foreach (var user in knownUsers)
						Console.WriteLine($"User: {user.FirstName} {user.LastName} (@{user.Username}) #{user.Id} speaks {user.LanguageCode}");
				}
			}
			Trace.WriteLine("Exiting");
		}

		private static async Task DoTask<T>(Func<T, Task> taskFunc, T e)
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

		internal static TResult[] SelectToArray<TSource, TResult>(this IReadOnlyList<TSource> source, Func<TSource, TResult> selector)
		{
			var result = new TResult[source.Count];
			for (int i = 0; i < result.Length; ++i)
				result[i] = selector(source[i]);
			return result;
		}

		internal static IEnumerable<int> Colors(this IEnumerable<int> cards) => cards.Select(c => c % 4);

		private static void CheckUser(User user)
		{
			if (knownUsers.Add(user))
				Trace.WriteLine($"User: {user.FirstName} {user.LastName} (@{user.Username}) #{user.Id} speaks {user.LanguageCode}");
		}
		#endregion

		private static readonly Dictionary<long, Game> gamesByChatId = new Dictionary<long, Game>();
		internal static readonly Dictionary<int, Game> gamesByUserId = new Dictionary<int, Game>();

		private static Game GetGame(Chat chat)
		{
			if (gamesByChatId.TryGetValue(chat.Id, out var game)) return game;
			return gamesByChatId[chat.Id] = new Game(Bot, chat.Id);
		}
		private static Game GetGame(User user)
		{
			return gamesByUserId.TryGetValue(user.Id, out var game) ? game : null;
		}

		private static async Task OnMessage(Message msg)
		{
			CheckUser(msg.From);
			Console.WriteLine($"{msg.From.Username}> {msg.Text}");
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
				await GetGame(msg.Chat).OnCommand(msg, command.ToLower(), args);
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
			CheckUser(query.From);
			var game = GetGame(query.From);
			if (game == null)
				await Bot.AnswerInlineQueryAsync(query.Id, null);
			else
				await game.OnInline(query);
		}
	}
}
