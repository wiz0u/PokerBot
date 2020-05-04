using System;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot.Types;

namespace PokerBot
{
	class Player
	{
		public User User;
		public int Stack;
		public int Bet;
		public enum PlayerStatus { DidNotTalk, Betting, Folded };
		public PlayerStatus Status;
		public readonly List<int> Cards = new List<int>();

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
