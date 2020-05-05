using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace PokerBot
{
	class Game
	{
		public readonly long ChatId;
		private readonly Telegram.Bot.TelegramBotClient Bot;
		readonly SemaphoreSlim sem = new SemaphoreSlim(1);

		int configTokens = 5000;
		int bigBlind = 20;
		int bigBet = 20;
		int maxBet = -1;

		enum Status { Inactive, CollectParticipants, WaitForBets };
		Status status;
		Message inscriptionMsg, lastMsg;
		string lastMsgText;
		readonly List<Player> Players = new List<Player>();
		IEnumerable<Player> PlayersInTurn => Players.Where(p => p.Status != Player.PlayerStatus.Folded);
		readonly List<int> Board = new List<int>();
		Queue<int> Deck;
		int buttonIdx;
		int currentBet;
		int currentPot;
		int bettingPlayers;
		Player CurrentPlayer;
		Player lastPlayerToRaise;
#pragma warning disable IDE0052 // Remove unread private members
		Task runNextTurn;
#pragma warning restore IDE0052 // Remove unread private members

		private static readonly string[] cardValues = new string[13] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };
		private static readonly string[] cardDescr = new string[13] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "Valet", "Dame", "Roi", "As" };
		private static readonly string[] handDescr = new string[13] { "au 2", "au 3", "au 4", "au 5", "au 6", "au 7", "au 8", "au 9", "au 10", "au Valet", "à la Dame", "au Roi", "à l'As" };
		private static readonly string[] handDescrs = new string[13] { "aux 2", "aux 3", "aux 4", "aux 5", "aux 6", "aux 7", "aux 8", "aux 9", "aux 10", "aux Valets", "aux Dames", "aux Rois", "aux As" };
		private static readonly string[] colorEmoji = new string[4] { "♣️", "♦️", "♥️", "♠️" };
		private static readonly string[] colorDescr = new string[4] { "trèfle", "carreau", "cœur", "pique" };
		private static string CardShort(int card) => $"{cardValues[card / 4]}{colorEmoji[card % 4]}";
		private static string CardFull(int card) => $"{cardDescr[card / 4]} de {colorDescr[card % 4]}";

		public Game(Telegram.Bot.TelegramBotClient bot, long id)
		{
			Bot = bot;
			ChatId = id;
		}

		public async Task OnCommand(Message msg, string cmd, string args)
		{
			await sem.WaitAsync();
			try
			{
				switch (cmd.ToLower())
				{
					case "/start": await OnCommandStart(msg, args); return;
					case "/stop": await OnCommandStop(msg); return;
					case "/b":
					case "/relance": await OnCommandRaise(msg, args); return;
					case "/check": await OnCommandCheck(msg, args); return;
					case "/stacks": await OnCommandStacks(msg); return;
				}
			}
			finally
			{
				sem.Release();
			}
		}

		private async Task OnCommandStart(Message msg, string arguments)
		{
			switch (msg.Chat.Type)
			{
				case ChatType.Channel:
				case ChatType.Private:
					await Bot.SendTextMessageAsync(ChatId, "Utilisez /start dans un groupe pour démarrer une partie");
					return;
			}
			switch (status)
			{
				case Status.Inactive:
					inscriptionMsg = null;
					break;
				case Status.CollectParticipants:
					if (DateTime.UtcNow - inscriptionMsg.Date < TimeSpan.FromMinutes(1))
						return;
					inscriptionMsg = null;
					break;
				case Status.WaitForBets:
					await Bot.SendTextMessageAsync(ChatId, "Une partie est déjà en cours avec " + string.Join(", ", Players));
					return;
			}
			var args = arguments.Split(' ');
			if (args.Length > 0 && int.TryParse(args[0], out int arg)) configTokens = arg;
			if (args.Length > 1 && int.TryParse(args[1], out arg)) bigBlind = arg;
			if (args.Length > 2 && int.TryParse(args[2], out arg))
			{
				if (arg / bigBlind <= 1 || arg % bigBlind != 0)
				{
					await Bot.SendTextMessageAsync(ChatId, "Montant invalide de mise maximale: " + arg);
					return;
				}
				maxBet = arg;
			}
			await DoCollectParticipants();
		}

		private async Task OnCommandStop(Message _)
		{
			if (status != Status.Inactive)
			{
				await Bot.SendTextMessageAsync(ChatId, "Fin de la partie");
				status = Status.Inactive;
			}
			foreach (var player in Players)
				Program.gamesByUserId.Remove(player.User.Id);
			CurrentPlayer = null;
			Players.Clear();
			Deck = null;
		}

		private async Task OnCommandRaise(Message msg, string arguments)
		{
			if (status != Status.WaitForBets)
				await Bot.SendTextMessageAsync(ChatId, "Commande valide seulement pendant les enchères");
			else if (msg.From.Id != CurrentPlayer.User.Id)
				await Bot.SendTextMessageAsync(ChatId, $"Ce n'est pas votre tour, c'est à {CurrentPlayer.MarkDown()} de parler", ParseMode.Markdown);
			else if (!double.TryParse(arguments, out double relance) || (relance *= bigBlind) < currentBet + bigBet - CurrentPlayer.Bet)
				await Bot.SendTextMessageAsync(ChatId, $"Vous devez relancer de +{BB(currentBet + bigBet - CurrentPlayer.Bet)} au minimum");
			else if (!CurrentPlayer.CanBet(CurrentPlayer.Bet + (int)relance))
				await Bot.SendTextMessageAsync(ChatId, $"Vous n'avez pas assez pour relancer d'autant", ParseMode.Markdown);
			else
				await DoChoice(CallbackWord.raise2, CurrentPlayer.Bet + (int)relance - currentBet);
		}

		private async Task OnCommandCheck(Message _, string arguments)
		{
			var cards = arguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(ParseCard).ToList();
			if (cards.IndexOf(-1) >= 0)
			{
				await Bot.SendTextMessageAsync(ChatId, "Carte non reconnue:\n"+string.Join("\n", cards.Select(c => c < 0 ? "???" : CardFull(c))));
				return;
			}
			var (force, descr) = DetermineBestHand(cards);
			var secondary = DetermineSecondary(cards);

			await Bot.SendTextMessageAsync(ChatId, $"Meilleure main: {descr} (force {force}, {secondary})");

			static int ParseCard(string s)
			{
				var couleur = Array.IndexOf(colorEmoji, s.Substring(s.Length-2));
				var rang = Array.IndexOf(cardValues, s.Remove(s.Length - 2));
				if (couleur == -1 || rang == -1) return -1;
				return couleur + rang * 4;
			}
		}

		private async Task OnCommandStacks(Message _)
		{
			if (status != Status.WaitForBets)
			{
				await Bot.SendTextMessageAsync(ChatId, "Pas de partie en cours", ParseMode.Markdown);
				return;
			}
			string text = "Stacks des joueurs :";
			foreach (var player in Players)
			{
				text += $"\n{player.MarkDown()}: {BB(player.Stack)}";
				if (player.Status == Player.PlayerStatus.Folded)
					text += " _(couché)_";
			}
			text += $"\nPot actuel: {BB(currentPot + Players.Sum(p => p.Bet))}";
			await Bot.SendTextMessageAsync(ChatId, text, ParseMode.Markdown);
		}

		private Player FindPlayer(InlineQuery query) => FindPlayer(query.From);
		private Player FindPlayer(CallbackQuery query) => FindPlayer(query.From);
		//private Player FindPlayer(Message msg) => FindPlayer(msg.From);

		private Player FindPlayer(User from)
		{
			if (from.Id == CurrentPlayer?.User.Id) return CurrentPlayer;
			return Players.Find(player => player.User.Id == from.Id);
		}

		enum CallbackWord { join = 0, start = 1, call = 2, raise = 3, raise2 = 4, fold = 5, check = 6 };
		public async Task OnCallback(CallbackQuery query, string[] words)
		{
			await sem.WaitAsync();
			try
			{
				var callback = Enum.Parse<CallbackWord>(words[0]);
				var player = FindPlayer(query);
				switch (status)
				{
					case Status.Inactive:
						await Bot.AnswerCallbackQueryAsync(query.Id, "Pas de partie en cours. Tapez /start pour commencer");
						return;
					case Status.CollectParticipants:
						if (callback >= CallbackWord.call)
						{
							await Bot.AnswerCallbackQueryAsync(query.Id, "Commande invalide");
							return;
						}
						break;
					case Status.WaitForBets:
						if (callback >= CallbackWord.call) break;
						if (player == null)
							await Bot.AnswerCallbackQueryAsync(query.Id, "La partie est déjà en cours. Attendez qu'elle finisse...");
						else
							await Bot.AnswerCallbackQueryAsync(query.Id);
						return;
				}
				switch (callback)
				{
					case CallbackWord.join: await DoJoin(query); break;
					case CallbackWord.start: await DoStart(query); break;
					case CallbackWord.call:
					case CallbackWord.raise:
					case CallbackWord.raise2:
					case CallbackWord.fold:
					case CallbackWord.check:
						if (player == null)
							await Bot.AnswerCallbackQueryAsync(query.Id, "Vous ne jouez pas dans cette partie !");
						else if (CurrentPlayer.User.Id != query.From.Id)
							await Bot.AnswerCallbackQueryAsync(query.Id, "Ce n'est pas votre tour de jouer !");
						else
						{
							await Bot.AnswerCallbackQueryAsync(query.Id);
							await DoChoice(callback);
						}
						break;
					default: await Bot.AnswerCallbackQueryAsync(query.Id, "Commande inconnue"); break;
				}
			}
			finally
			{
				sem.Release();
			}
		}

		private async Task DoJoin(CallbackQuery query)
		{
			var player = FindPlayer(query);
			if (player != null)
			{
				Players.Add(new Player { User = new User { Id = query.From.Id, FirstName = query.From.FirstName + (Players.Count + 1), Username = query.From.Username } });
				//await Bot.AnswerCallbackQueryAsync(query.Id, "Vous êtes déjà inscrit !");
				//return;
			}
			else
				Players.Add(new Player { User = query.From });
			Program.gamesByUserId[query.From.Id] = this;
			await Bot.AnswerCallbackQueryAsync(query.Id, "Vous êtes inscrit");
			await DoCollectParticipants();
		}

		private async Task DoCollectParticipants(bool finalize = false)
		{
			status = Status.CollectParticipants;
			var replyMarkup = new List<InlineKeyboardButton>();
			if (!finalize)
			{
				replyMarkup.Add(InlineKeyboardButton.WithCallbackData("Moi !", "join " + ChatId));
				if (Players.Count >= 2)
					replyMarkup.Add(InlineKeyboardButton.WithCallbackData("Commencer la partie", "start " + ChatId));
			}
			var text = $"Jetons: {configTokens}, Big Blind: {bigBlind}";
			if (maxBet != -1) text += $" max {maxBet}";
			if (finalize)
				text = $"La partie commence! {text}\nParticipants: ";
			else
				text = $"La partie va commencer... {text}\nQui participe ? ";
			text += string.Join(", ", Players.Select(player => player.MarkDown()));
			if (inscriptionMsg == null)
				inscriptionMsg = await Bot.SendTextMessageAsync(ChatId, text, ParseMode.Markdown,
					replyMarkup: new InlineKeyboardMarkup(replyMarkup));
			else
				await Bot.EditMessageTextAsync(inscriptionMsg.Chat, inscriptionMsg.MessageId, text, ParseMode.Markdown,
					replyMarkup: replyMarkup.ToArray());
		}

		private async Task DoStart(CallbackQuery query)
		{
			if (FindPlayer(query) == null)
			{
				await Bot.AnswerCallbackQueryAsync(query.Id, "Seul un joueur inscrit peut lancer la partie");
				return;
			}
			await Bot.AnswerCallbackQueryAsync(query.Id, "C'est parti !");
			await DoCollectParticipants(true);
			status = Status.WaitForBets;
			foreach (var player in Players)
				player.Stack = configTokens;
			buttonIdx = Players.Count - 1;

			await StartTurn();
		}

		private async Task StartTurn()
		{
			ShuffleCards();
			Board.Clear();
			bettingPlayers = Players.Count;
			currentPot = 0;
			foreach (var player in Players)
			{
				player.Cards.Clear();
				player.Bet = 0;
				player.Status = Player.PlayerStatus.DidNotTalk;
			}
			foreach (var player in Players)
				player.Cards.Add(Deck.Dequeue());
			foreach (var player in Players)
				player.Cards.Add(Deck.Dequeue());
			int smallIdx = (buttonIdx + 1) % Players.Count;
			int bigIdx = (smallIdx + 1) % Players.Count;
			Players[smallIdx].SetBet(bigBlind/2);
			Players[bigIdx].SetBet(bigBlind);
			currentBet = bigBlind;
			bigBet = bigBlind;
			CurrentPlayer = Players[(bigIdx + 1) % Players.Count];
			lastPlayerToRaise = CurrentPlayer;
			var choices = GetChoices();
			lastMsg = await Bot.SendTextMessageAsync(ChatId,
				lastMsgText = "Nouveau tour, nouvelles cartes!\n" +
				$"{Players[smallIdx]}: petite blind. {Players[bigIdx]}: grosse blind.\n" +
				$"C'est à {CurrentPlayer.MarkDown()} de parler",
				ParseMode.Markdown,
				replyMarkup: new InlineKeyboardMarkup(new IEnumerable<InlineKeyboardButton>[] {
					choices,
					new[] { InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("Voir mes cartes", "") },
			}));
		}

		private List<InlineKeyboardButton> GetChoices()
		{
			var choices = new List<InlineKeyboardButton>();
			if (CurrentPlayer.Bet == currentBet)
				choices.Add(InlineKeyboardButton.WithCallbackData("Parler", "check " + ChatId));
			else if (CurrentPlayer.CanBet(currentBet))
				choices.Add(InlineKeyboardButton.WithCallbackData($"Suivre +{BB(currentBet - CurrentPlayer.Bet)}", "call " + ChatId));
			if ((maxBet == -1 || currentBet + bigBet < maxBet) && CurrentPlayer.CanBet(currentBet + bigBet))
				choices.Add(InlineKeyboardButton.WithCallbackData($"Relance +{BB(currentBet + bigBet - CurrentPlayer.Bet)}", "raise " + ChatId));
			if (CurrentPlayer.Bet < currentBet)
				choices.Add(InlineKeyboardButton.WithCallbackData("Passer", "fold " + ChatId));
			return choices;
		}

		private async Task DoChoice(CallbackWord choice, int customValue = 0)
		{
			string text = CurrentPlayer.ToString();
			switch (choice)
			{
				case CallbackWord.call:
					text += " *suit*.";
					CurrentPlayer.SetBet(currentBet);
					break;
				case CallbackWord.raise:
					lastPlayerToRaise = CurrentPlayer;
					currentBet += bigBet;
					text += $" *relance de {BB(currentBet - CurrentPlayer.Bet)}* !";
					CurrentPlayer.SetBet(currentBet);
					break;
				case CallbackWord.raise2:
					lastPlayerToRaise = CurrentPlayer;
					bigBet = customValue;
					currentBet += bigBet;
					text += $" *relance de {BB(currentBet - CurrentPlayer.Bet)}* !";
					CurrentPlayer.SetBet(currentBet);
					break;
				case CallbackWord.fold:
					bettingPlayers--;
					text += " *passe*.";
					CurrentPlayer.Status = Player.PlayerStatus.Folded;
					break;
				case CallbackWord.check:
					text += " *parle*.";
					break;
				default:
					break;
			}
			await RemoveLastMessageButtons();
			var pot = currentPot + Players.Sum(p => p.Bet);
			if (bettingPlayers == 1)
			{
				var winner = PlayersInTurn.Single();
				winner.Stack += pot;
				foreach (var player in Players)
					player.Bet = 0;
				await Bot.SendTextMessageAsync(ChatId,
					$"{text}\n{winner.MarkDown()} remporte le pot de {BB(pot)}",
					ParseMode.Markdown);
				buttonIdx = (buttonIdx + 1) % Players.Count;
				runNextTurn = Task.Delay(5000).ContinueWith(_ => StartTurn());
			}
			else
			{
				var stillBetting = NextPlayer();
#if false //abattageImmediat
				DistributeBoard(); DistributeBoard(); DistributeBoard(); stillBetting = false;
#endif
				if (stillBetting)
					text += $" La mise est à {BB(currentBet)}\nC'est au tour de {CurrentPlayer.MarkDown()} de parler";
				else if (Board.Count == 5)
				{
					text += $" Abattage des cartes !\nBoard: ";
					text += string.Join("  ", Board.Select(CardShort));
					var hands = PlayersInTurn.Select(player => (player, hand:DetermineBestHand(Board.Concat(player.Cards)))).ToList();
					foreach (var (player, hand) in hands)
						text += $"\n{string.Join("  ", player.Cards.Select(CardShort))} : {hand.descr} pour {player}";
					var winners = DetermineWinners(hands);
					if (winners.Count > 1)
					{
						text += $"\nEgalité entre {string.Join(" et ", winners)} ! Partage du pot: {BB(pot)}";
						foreach (var player in winners)
							player.Stack += pot / winners.Count;
					}
					else
					{
						text += $"\n{winners[0].MarkDown()} remporte le pot de {BB(pot)} !";
						winners[0].Stack += pot;
					}
					await Bot.SendTextMessageAsync(ChatId, text, ParseMode.Markdown);
					buttonIdx = (buttonIdx + 1) % Players.Count;
					runNextTurn = Task.Delay(5000).ContinueWith(_ => StartTurn());
					return;
				}
				else
				{
					text += $" Le tour d'enchères est terminé. Pot: {BB(pot)}\n{DistributeBoard()}: ";
					text += string.Join("  ", Board.Select(CardShort));
					currentPot = pot;
					foreach (var player in Players)
						player.Bet = 0;
					currentBet = 0;
					bigBet = bigBlind;
					lastPlayerToRaise = null;
					CurrentPlayer = Players[buttonIdx];
					NextPlayer();
					lastPlayerToRaise = CurrentPlayer;
					text += $"\nC'est au tour de {CurrentPlayer.MarkDown()} de parler";
				}
				var choices = GetChoices();
				lastMsg = await Bot.SendTextMessageAsync(ChatId,
					lastMsgText = text,
					ParseMode.Markdown,
					replyMarkup: new InlineKeyboardMarkup(new IEnumerable<InlineKeyboardButton>[] {
						choices,
						new[] { InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("Voir mes cartes", "") },
					}));
			}
		}

		private List<Player> DetermineWinners(List<(Player player, (int force, string descr) hand)> hands)
		{
			var winners = new List<Player>();
			int winForce = 0;
			foreach (var (player, hand) in hands)
			{
				if (hand.force < winForce) continue;
				if (hand.force > winForce) winners.Clear();
				winForce = hand.force;
				winners.Add(player);
			}
			if (winners.Count == 1)
				return winners;
			var winners2 = new List<Player>();
			winForce = 0;
			foreach (var player in winners)
			{
				var force = DetermineSecondary(Board.Concat(player.Cards));
				if (force < winForce) continue;
				if (force > winForce) winners2.Clear();
				winForce = force;
				winners2.Add(player);
			}
			return winners2;
		}

		private (int force, string descr) DetermineBestHand(IEnumerable<int> cards)
		{
			var lookup = cards.ToLookup(c => c / 4);
			int suite = 0;
			for (int i = 12; i >= 4; --i)
			{
				if (lookup[i].Any() && lookup[i - 1].Any() && lookup[i - 2].Any() && lookup[i - 3].Any() && lookup[i - 4].Any())
				{
					suite = i;
					break;
				}
			}
			if (suite == 0 && lookup[3].Any() && lookup[2].Any() && lookup[1].Any() && lookup[0].Any() && lookup[12].Any())
				suite = 3; // suite au 4
			if (suite != 0)
			{
				bool flush = lookup[suite].Colors()
					.Intersect(lookup[suite - 1].Colors())
					.Intersect(lookup[suite - 2].Colors())
					.Intersect(lookup[suite - 3].Colors())
					.Intersect(lookup[(suite + 9) % 13].Colors())
					.Any();
				if (flush)
					return suite == 12 ? (80014, "quinte flush royale") : (80002 + suite, "quinte flush " + handDescr[suite]);
			}
			var orderedLookup = lookup.OrderByDescending(g => g.Key).ToList();
			var carre = orderedLookup.Find(g => g.Count() == 4)?.Key;
			if (carre.HasValue) return (70002 + carre.Value, "carré " + handDescrs[carre.Value]);
			var brelan = orderedLookup.Find(g => g.Count() == 3)?.Key;
			var paires = orderedLookup.Where(g => g.Count() == 2).ToList();
			if (brelan.HasValue && paires.Count > 0)
				return (60202 + brelan.Value * 100 + paires[0].Key, "full " + handDescrs[brelan.Value] + " et " + handDescr[paires[0].Key]);
			var couleur = cards.ToLookup(c => c % 4).Where(g => g.Count() >= 5).ToList();
			if (couleur.Count > 0)
			{
				var rangCouleur = couleur.SelectMany(g => g).Max() / 4;
				return (50002 + rangCouleur, "couleur " + handDescr[rangCouleur]);
			}
			if (suite != 0)
				return (40002 + suite, "suite " + handDescr[suite]);
			if (brelan.HasValue)
				return (30002 + brelan.Value, "brelan " + handDescrs[brelan.Value]);
			if (paires.Count >= 2)
			{
				int p1 = paires[0].Key, p2 = paires[1].Key;
				return (20202 + p1 * 100 + p2, "double paire " + handDescrs[p1] + " et " + handDescrs[p2]);
			}
			if (paires.Count == 1)
				return (10002 + paires[0].Key, "paire " + handDescrs[paires[0].Key]);
			var rang = orderedLookup[0].Key;
			return (2 + rang, cardDescr[rang]);
		}

		private int DetermineSecondary(IEnumerable<int> cards)
		{
			int force = 0;
			foreach (var card in cards.Select(c => c / 4 + 2).OrderByDescending(c => c).Take(5))
				force = force * 100 + card;
			return force;
		}

		static readonly string[] BoardNames = new[] { "Flop", "Turn", "River" };
		private string DistributeBoard()
		{
			Deck.Dequeue(); // carte brûlée
			if (Board.Count == 0)
			{
				Board.Add(Deck.Dequeue());
				Board.Add(Deck.Dequeue());
			}
			Board.Add(Deck.Dequeue());
			return BoardNames[Board.Count - 3];
		}

		private async Task RemoveLastMessageButtons()
		{
			if (lastMsg == null) return;
			await Bot.EditMessageTextAsync(lastMsg.Chat, lastMsg.MessageId, lastMsgText, ParseMode.Markdown, replyMarkup: null);
		}

		private bool NextPlayer()
		{
			int currentIndex = Players.IndexOf(CurrentPlayer);
			do
			{
				currentIndex = (currentIndex + 1) % Players.Count;
				CurrentPlayer = Players[currentIndex];
				if (CurrentPlayer == lastPlayerToRaise) return false;
			} while (CurrentPlayer.Status == Player.PlayerStatus.Folded);
			return true;
		}

		private void ShuffleCards()
		{
			var cards = new int[52];
			var rand = new Random();
			// Knuth algorithm
			for (int i = 0; i < 52; i++)
				cards[i] = i;
			for (int i = 0; i < 52; i++)
			{
				int j = rand.Next(i, cards.Length); // Don't select from the entire array on subsequent loops
				int temp = cards[i]; cards[i] = cards[j]; cards[j] = temp;
			}
			Deck = new Queue<int>(cards);
		}

		public async Task OnInline(InlineQuery query)
		{
			await sem.WaitAsync();
			try
			{
				string expr = query.Query;
				var player = FindPlayer(query);
				if (player == null)
				{
					await Bot.AnswerInlineQueryAsync(query.Id, null);
					return;
				}
				string descr = null;
				if (Board.Count >= 3)
					descr = "Votre main: " + DetermineBestHand(Board.Concat(player.Cards)).descr;
				await Bot.AnswerInlineQueryAsync(query.Id,
					new[] {
						new InlineQueryResultArticle($"Card_"+string.Join("_", player.Cards),
							"Vos cartes:  " + string.Join("  ", player.Cards.Select(CardShort)),
							new InputTextMessageContent("(je regarde mes cartes)"))
						{ Description = descr },//, ThumbUrl = "https://raw.githubusercontent.com/hayeah/playing-cards-assets/master/png/2_of_hearts.png", ThumbWidth = 222, ThumbHeight = 323 },
					  //new InlineQueryResultPhoto("2", $"http://via.placeholder.com/{expr}", $"http://via.placeholder.com/{expr}") { Title = "photo", PhotoWidth = 85, PhotoHeight = 85 },
					},
					5, isPersonal: true, switchPmText: $"Mise: {BB(player.Bet)} | Stack: {BB(player.Stack)}", switchPmParameter: "cards");
			}
			finally
			{
				sem.Release();
			}
		}

		private string BB(int montant)
		{
			if (montant % bigBlind == 0)
				return $"{montant / bigBlind} BB";
			else
				return $"{(double)montant / bigBlind:N1} BB";
		}
	}
}
