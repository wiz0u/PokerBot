using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace PokerBot
{
	public class TelegramWrapper
	{
		private readonly TelegramBotClient Bot;

		public TelegramWrapper(TelegramBotClient bot) => Bot = bot;

		public Task<Message> SendMsg(Chat chat, string text, IReplyMarkup replyMarkup = null)
		{
			Console.WriteLine($"{chat.Title}< {text.Replace("\n", " ")}");
			return Bot.SendTextMessageAsync(chat.Id, text, ParseMode.Markdown, replyMarkup: replyMarkup);
		}

		public Task AnswerCallback(CallbackQuery query, string text = null)
		{
			if (text != null) Console.WriteLine($"{query.From.Username}: {text}");
			return Bot.AnswerCallbackQueryAsync(query.Id, text);
		}

		public Task AnswerInline(InlineQuery inlineQuery, IEnumerable<InlineQueryResultBase> results = null, int? cacheTime = null, bool isPersonal = false, string nextOffset = null, string switchPmText = null, string switchPmParameter = null)
			=> Bot.AnswerInlineQueryAsync(inlineQuery.Id, results ?? Enumerable.Empty<InlineQueryResultBase>(), cacheTime ?? 2, isPersonal, nextOffset, switchPmText, switchPmParameter);

		public Task<Message> EditMsg(Message msg, string text, InlineKeyboardMarkup replyMarkup = null)
			=> Bot.EditMessageTextAsync(msg.Chat, msg.MessageId, text, ParseMode.Markdown, replyMarkup: replyMarkup);
	}
}
