using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
		public readonly Chat Chat;
		public readonly long ChatId;
		private readonly TelegramWrapper Telegram;
		readonly SemaphoreSlim sem = new SemaphoreSlim(1);

		// game config:
		int InitialTokens = 5000;
		int BigBlind = 20;
		int MaxBet = -1; // default: No Limit

		// game status:
		enum Status { Inactive, CollectParticipants, WaitForBets };
		Status status;
		readonly List<Player> Players = new List<Player>();
		IEnumerable<Player> PlayersInTurn => Players.Where(p => p.Status != Player.PlayerStatus.Folded);
		readonly List<Card> Board = new List<Card>();
		Queue<Card> Deck;
		int ButtonIdx;
		int BigBet;
		int CurrentBet;
		int CurrentPot;
		Player CurrentPlayer;
		Player LastPlayerToRaise;

#pragma warning disable IDE0052 // Remove unread private members
		Message inscriptionMsg, lastMsg;
		string lastMsgText;
		Task runNextTurn;
#pragma warning restore IDE0052 // Remove unread private members

		private string BB(int montant) => montant % BigBlind == 0 ? $"{montant / BigBlind} BB" : $"{(double)montant / BigBlind:N1} BB";
		private Player FindPlayer(User from) => from.Id == CurrentPlayer?.User.Id ? CurrentPlayer : Players.Find(player => player.User.Id == from.Id);
		private Player FindPlayer(InlineQuery query) => FindPlayer(query.From);
		private Player FindPlayer(CallbackQuery query) => FindPlayer(query.From);
		//private Player FindPlayer(Message msg) => FindPlayer(msg.From);

		public Game(TelegramWrapper telegram, Chat chat)
		{
			Telegram = telegram;
			Chat = chat;
			ChatId = chat.Id;
		}

		// point d'entrée pour les commandes 'slash'
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
					await Telegram.SendMsg(Chat, "Utilisez /start dans un groupe pour démarrer une partie");
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
					await Telegram.SendMsg(Chat, "Une partie est déjà en cours avec " + string.Join(", ", Players));
					return;
			}
			var args = arguments.Split(' ');
			if (args.Length > 0 && int.TryParse(args[0], out int arg)) InitialTokens = arg;
			if (args.Length > 1 && int.TryParse(args[1], out arg)) BigBlind = arg;
			if (args.Length > 2 && int.TryParse(args[2], out arg))
			{
				if (arg / BigBlind <= 1 || arg % BigBlind != 0)
				{
					await Telegram.SendMsg(Chat, "Montant invalide de mise maximale: " + arg);
					return;
				}
				MaxBet = arg;
			}
			Trace.TraceInformation($"Ouverture de la partie sur {Chat.Title}");
			await DoCollectParticipants();
		}

		private async Task OnCommandStop(Message _)
		{
			if (status != Status.Inactive)
			{
				await Telegram.SendMsg(Chat, "Fin de la partie");
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
				await Telegram.SendMsg(Chat, "Commande valide seulement pendant les enchères");
			else if (msg.From.Id != CurrentPlayer.User.Id)
				await Telegram.SendMsg(Chat, $"Ce n'est pas votre tour, c'est à {CurrentPlayer.MarkDown()} de parler");
			else if (!double.TryParse(arguments, out double relance) || (relance *= BigBlind) < CurrentBet + BigBet - CurrentPlayer.Bet)
				await Telegram.SendMsg(Chat, $"Vous devez relancer de +{BB(CurrentBet + BigBet - CurrentPlayer.Bet)} au minimum");
			else if (!CurrentPlayer.CanBet(CurrentPlayer.Bet + (int)relance))
				await Telegram.SendMsg(Chat, $"Vous n'avez pas assez pour relancer d'autant");
			else
				await DoChoice(CallbackWord.raise2, CurrentPlayer.Bet + (int)relance - CurrentBet);
		}

		private async Task OnCommandCheck(Message _, string arguments)
		{
			var cards = arguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Card.Parse).ToList();
			if (cards.IndexOf(Card.Invalid) >= 0)
			{
				await Telegram.SendMsg(Chat, "Carte non reconnue:\n" + string.Join("\n", cards.Select(c => c.ToFullString())));
				return;
			}
			var (force, descr) = Card.BestHand(cards);
			var secondary = Card.CardsForce(cards);

			await Telegram.SendMsg(Chat, $"Meilleure main: {descr} (force {force}, {secondary})");
		}

		private async Task OnCommandStacks(Message _)
		{
			if (status != Status.WaitForBets)
			{
				await Telegram.SendMsg(Chat, "Pas de partie en cours");
				return;
			}
			string text = "Stacks des joueurs :";
			foreach (var player in Players)
			{
				text += $"\n{player.MarkDown()}: {BB(player.Stack)}";
				if (player.Status == Player.PlayerStatus.Folded)
					text += " _(couché)_";
			}
			text += $"\nPot actuel: {BB(CurrentPot + Players.Sum(p => p.Bet))}";
			await Telegram.SendMsg(Chat, text);
		}

		// point d'entrée pour la complétion inline
		public async Task OnInline(InlineQuery query)
		{
			await sem.WaitAsync();
			try
			{
				string expr = query.Query;
				var player = FindPlayer(query);
				if (player == null)
				{
					await Telegram.AnswerInline(query);
					return;
				}
				string descr = null;
				if (Board.Count >= 3)
					descr = "Votre main: " + Card.BestHand(Board.Concat(player.Cards)).descr;
				await Telegram.AnswerInline(query,
					new[] {
						new InlineQueryResultArticle($"Card_"+string.Join("_", player.Cards),
							"Vos cartes:  " + string.Join("  ", player.Cards),
							new InputTextMessageContent("(je regarde mes cartes)"))
						{ Description = descr },//, ThumbUrl = "https://raw.githubusercontent.com/hayeah/playing-cards-assets/master/png/2_of_hearts.png", ThumbWidth = 222, ThumbHeight = 323 },
					  //new InlineQueryResultPhoto("2", $"http://via.placeholder.com/{expr}", $"http://via.placeholder.com/{expr}") { Title = "photo", PhotoWidth = 85, PhotoHeight = 85 },
					},
					2, isPersonal: true, switchPmText: $"Mise: {BB(player.Bet)} | Stack: {BB(player.Stack)}", switchPmParameter: "cards");
			}
			finally
			{
				sem.Release();
			}
		}

		// point d'entrée pour les boutons sous mes messages
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
						await Telegram.AnswerCallback(query, "Pas de partie en cours. Tapez /start pour commencer");
						return;
					case Status.CollectParticipants:
						if (callback >= CallbackWord.call)
						{
							await Telegram.AnswerCallback(query, "Commande invalide");
							return;
						}
						break;
					case Status.WaitForBets:
						if (callback >= CallbackWord.call) break;
						if (player == null)
							await Telegram.AnswerCallback(query, "La partie est déjà en cours. Attendez qu'elle finisse...");
						else
							await Telegram.AnswerCallback(query);
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
							await Telegram.AnswerCallback(query, "Vous ne jouez pas dans cette partie !");
						else if (CurrentPlayer.User.Id != query.From.Id)
							await Telegram.AnswerCallback(query, "Ce n'est pas votre tour de jouer !");
						else
						{
							await Telegram.AnswerCallback(query);
							await DoChoice(callback);
						}
						break;
					default: await Telegram.AnswerCallback(query, "Commande inconnue"); break;
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
#if DEBUG
				Players.Add(new Player { User = new User { Id = query.From.Id, FirstName = query.From.FirstName + (Players.Count + 1), Username = query.From.Username } });
#else
				await Bot.AnswerCallback(query, "Vous êtes déjà inscrit !");
				return;
#endif
			}
			else
				Players.Add(new Player { User = query.From });
			Program.gamesByUserId[query.From.Id] = this;
			await Telegram.AnswerCallback(query, "Vous êtes inscrit");
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
			var text = $"Jetons: {InitialTokens}, Big Blind: {BigBlind}";
			if (MaxBet != -1) text += $" max {MaxBet}";
			if (finalize)
				text = $"La partie commence! {text}\nParticipants: ";
			else
				text = $"La partie va commencer... {text}\nQui participe ? ";
			text += string.Join(", ", Players.Select(player => player.MarkDown()));
			if (inscriptionMsg == null)
				inscriptionMsg = await Telegram.SendMsg(Chat, text, new InlineKeyboardMarkup(replyMarkup));
			else
				await Telegram.EditMsg(inscriptionMsg, text, replyMarkup.ToArray());
		}

		private async Task DoStart(CallbackQuery query)
		{
			if (FindPlayer(query) == null)
			{
				await Telegram.AnswerCallback(query, "Seul un joueur inscrit peut lancer la partie");
				return;
			}
			Trace.TraceInformation($"Lancement de la partie sur {Chat.Title} avec {string.Join(", ", Players)}");
			await Telegram.AnswerCallback(query, "C'est parti !");
			await DoCollectParticipants(true);
			status = Status.WaitForBets;
			foreach (var player in Players)
				player.Stack = InitialTokens;
			ButtonIdx = Players.Count - 1;

			await StartTurn();
		}

		private void ShuffleCards() // Knuth algorithm
		{
			var rand = new Random();
			var cards = Enumerable.Range(0, 52).Select(i => new Card(i)).ToArray();
			for (int i = 0; i < 52; i++)
			{
				int j = rand.Next(i, cards.Length);
				Card temp = cards[i]; cards[i] = cards[j]; cards[j] = temp; // swap cards i&j
			}
			Deck = new Queue<Card>(cards);
		}

		private async Task StartTurn()
		{
			ShuffleCards();
			Board.Clear();
			CurrentPot = 0;
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
			int smallIdx = (ButtonIdx + 1) % Players.Count;
			int bigIdx = (smallIdx + 1) % Players.Count;
			Players[smallIdx].SetBet(BigBlind/2);
			Players[bigIdx].SetBet(BigBlind);
			CurrentBet = BigBlind;
			BigBet = BigBlind;
			CurrentPlayer = Players[(bigIdx + 1) % Players.Count];
			LastPlayerToRaise = CurrentPlayer;
			lastMsg = await Telegram.SendMsg(Chat,
				lastMsgText = "Nouveau tour, nouvelles cartes!\n" +
				$"{Players[smallIdx]}: petite blind. {Players[bigIdx]}: grosse blind.\n" +
				$"C'est à {CurrentPlayer.MarkDown()} de parler",
				GetChoices());
		}

		private InlineKeyboardMarkup GetChoices()
		{
			var choices = new List<InlineKeyboardButton>();
			if (CurrentPlayer.Bet == CurrentBet)
				choices.Add(InlineKeyboardButton.WithCallbackData("Parler", "check " + ChatId));
			else if (CurrentPlayer.CanBet(CurrentBet))
				choices.Add(InlineKeyboardButton.WithCallbackData($"Suivre +{BB(CurrentBet - CurrentPlayer.Bet)}", "call " + ChatId));
			if ((MaxBet == -1 || CurrentBet + BigBet < MaxBet) && CurrentPlayer.CanBet(CurrentBet + BigBet))
				choices.Add(InlineKeyboardButton.WithCallbackData($"Relance +{BB(CurrentBet + BigBet - CurrentPlayer.Bet)}", "raise " + ChatId));
			if (CurrentPlayer.Bet < CurrentBet)
				choices.Add(InlineKeyboardButton.WithCallbackData("Passer", "fold " + ChatId));
			return new InlineKeyboardMarkup(new IEnumerable<InlineKeyboardButton>[] {
				choices,
				new[] { InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("Voir mes cartes", "") },
			});
		}

		private async Task DoChoice(CallbackWord choice, int customValue = 0)
		{
			string text = CurrentPlayer.ToString();
			switch (choice)
			{
				case CallbackWord.call:
					text += " *suit*.";
					CurrentPlayer.SetBet(CurrentBet);
					break;
				case CallbackWord.raise:
					LastPlayerToRaise = CurrentPlayer;
					CurrentBet += BigBet;
					text += $" *relance de {BB(CurrentBet - CurrentPlayer.Bet)}* !";
					CurrentPlayer.SetBet(CurrentBet);
					break;
				case CallbackWord.raise2:
					LastPlayerToRaise = CurrentPlayer;
					BigBet = customValue;
					CurrentBet += BigBet;
					text += $" *relance de {BB(CurrentBet - CurrentPlayer.Bet)}* !";
					CurrentPlayer.SetBet(CurrentBet);
					break;
				case CallbackWord.fold:
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
			var pot = CurrentPot + Players.Sum(p => p.Bet);
			if (PlayersInTurn.Count() == 1)
			{
				var winner = PlayersInTurn.Single();
				winner.Stack += pot;
				foreach (var player in Players)
					player.Bet = 0;
				await Telegram.SendMsg(Chat, $"{text}\n{winner.MarkDown()} remporte le pot de {BB(pot)}");
				ButtonIdx = (ButtonIdx + 1) % Players.Count;
				runNextTurn = Task.Delay(5000).ContinueWith(_ => StartTurn());
			}
			else
			{
				Trace.TraceInformation($"Pot sur {Chat.Title}: {CurrentPot} + {string.Join(" ", Players.Select(p => $"{p}:{p.Bet}"))}");
				var stillBetting = NextPlayer();
#if ABATTAGE_IMMEDIAT
				DistributeBoard(); DistributeBoard(); DistributeBoard(); stillBetting = false;
#endif
				if (stillBetting)
					text += $" La mise est à {BB(CurrentBet)}\nC'est au tour de {CurrentPlayer.MarkDown()} de parler";
				else if (Board.Count == 5)
				{
					text += $" Abattage des cartes !\nBoard: ";
					text += string.Join("  ", Board);
					var hands = PlayersInTurn.Select(player => (player, hand: Card.BestHand(Board.Concat(player.Cards)))).ToList();
					foreach (var (player, hand) in hands)
						text += $"\n{string.Join("  ", player.Cards)} : {hand.descr} pour {player}";
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
					await Telegram.SendMsg(Chat, text);
					ButtonIdx = (ButtonIdx + 1) % Players.Count;
					runNextTurn = Task.Delay(5000).ContinueWith(_ => StartTurn());
					return;
				}
				else
				{
					text += $" Le tour d'enchères est terminé. Pot: {BB(pot)}\n{DistributeBoard()}: ";
					text += string.Join("  ", Board);
					CurrentPot = pot;
					foreach (var player in Players)
						player.Bet = 0;
					CurrentBet = 0;
					BigBet = BigBlind;
					LastPlayerToRaise = null;
					CurrentPlayer = Players[ButtonIdx];
					NextPlayer();
					LastPlayerToRaise = CurrentPlayer;
					text += $"\nC'est au tour de {CurrentPlayer.MarkDown()} de parler";
				}
				lastMsg = await Telegram.SendMsg(Chat, lastMsgText = text, GetChoices());
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
				var force = Card.CardsForce(Board.Concat(player.Cards));
				if (force < winForce) continue;
				if (force > winForce) winners2.Clear();
				winForce = force;
				winners2.Add(player);
			}
			return winners2;
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
			await Telegram.EditMsg(lastMsg, lastMsgText);
		}

		private bool NextPlayer()
		{
			int currentIndex = Players.IndexOf(CurrentPlayer);
			do
			{
				currentIndex = (currentIndex + 1) % Players.Count;
				CurrentPlayer = Players[currentIndex];
				if (CurrentPlayer == LastPlayerToRaise) return false;
			} while (CurrentPlayer.Status == Player.PlayerStatus.Folded);
			return true;
		}
	}
}
