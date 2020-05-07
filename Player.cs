using System;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot.Types;

namespace PokerBot
{
	class Player
	{
		public User User;
		public int Stack;	// jetons restants
		public int Bet;		// mise totale actuelle sur cette manche
		public enum PlayerStatus { DidNotTalk, Betting, Folded };
		public PlayerStatus Status;
		public readonly List<Card> Cards = new List<Card>();

		public override string ToString() => User.FirstName;
		public string MarkDown() => $"[{User.FirstName}](tg://user?id={User.Id})";
		public void SetBet(int newBet)
		{
			Stack -= (newBet - Bet);
			Bet = newBet;
		}
		public bool CanBet(int targetBet) => Stack >= (targetBet - Bet);
	}
}
