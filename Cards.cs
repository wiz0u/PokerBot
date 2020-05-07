using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace PokerBot
{
	public struct Card : IComparable<Card>
	{
		public static Card Invalid = new Card(-1);
		private readonly int _value;

		private static readonly string[] ShortNames = new string[13] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };
		private static readonly string[] FullNames = new string[13] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "Valet", "Dame", "Roi", "As" };
		private static readonly string[] HandQualifier_au = new string[13] { "au 2", "au 3", "au 4", "au 5", "au 6", "au 7", "au 8", "au 9", "au 10", "au Valet", "à la Dame", "au Roi", "à l'As" };
		private static readonly string[] HandQualifier_aux = new string[13] { "aux 2", "aux 3", "aux 4", "aux 5", "aux 6", "aux 7", "aux 8", "aux 9", "aux 10", "aux Valets", "aux Dames", "aux Rois", "aux As" };
		private static readonly string[] HandQualifiers_de = new string[13] { "de 2", "de 3", "de 4", "de 5", "de 6", "de 7", "de 8", "de 9", "de 10", "de Valets", "de Dames", "de Rois", "d'As" };
		private static readonly string[] ColorEmojis = new string[4] { "♣", "♦", "♥", "♠" };
		private static readonly string[] ColorNames = new string[4] { "trèfle", "carreau", "cœur", "pique" };

		public Card(int value) => _value = value;
		public int Color => _value % 4;
		public static bool operator ==(Card left, Card right) => left._value == right._value;
		public static bool operator !=(Card left, Card right) => left._value != right._value;
		public override bool Equals(object obj) => obj is Card card && _value == card._value;
		public override int GetHashCode() => _value;
		public override string ToString() => _value < 0 ? "???" : $"{ShortNames[_value / 4]}{ColorEmojis[_value % 4]}";
		public string ToFullString() => _value < 0 ? "???" : $"{FullNames[_value / 4]} de {ColorNames[_value % 4]}";
		public int CompareTo([AllowNull] Card other) => _value.CompareTo(other._value);

		public static Card Parse(string str)
		{
			str = str.TrimEnd('\xFE0F');
			var couleur = Array.IndexOf(ColorEmojis, str.Substring(str.Length - 1));
			var rang = Array.IndexOf(ShortNames, str.Remove(str.Length - 1));
			if (couleur == -1 || rang == -1) return Invalid;
			return new Card(couleur + rang * 4);
		}

		public static (int force, string descr) BestHand(IEnumerable<Card> cards)
		{
			var lookup = cards.ToLookup(c => c._value / 4);
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
					return suite == 12 ? (80014, "quinte flush royale") : (80002 + suite, "quinte flush " + HandQualifier_au[suite]);
			}
			var orderedLookup = lookup.OrderByDescending(g => g.Key).ToList();
			var carre = orderedLookup.Find(g => g.Count() == 4)?.Key;
			if (carre.HasValue) return (70002 + carre.Value, "carré " + HandQualifiers_de[carre.Value]);
			var brelan = orderedLookup.Find(g => g.Count() == 3)?.Key;
			var paires = orderedLookup.Where(g => g.Count() == 2).ToList();
			if (brelan.HasValue && paires.Count > 0)
				return (60202 + brelan.Value * 100 + paires[0].Key, "full " + HandQualifier_aux[brelan.Value] + " et " + HandQualifier_au[paires[0].Key]);
			var couleur = cards.ToLookup(c => c.Color).Where(g => g.Count() >= 5).ToList();
			if (couleur.Count > 0)
			{
				var rangCouleur = couleur.SelectMany(g => g).Max()._value / 4;
				return (50002 + rangCouleur, "couleur " + HandQualifier_au[rangCouleur]);
			}
			if (suite != 0)
				return (40002 + suite, "suite " + HandQualifier_au[suite]);
			if (brelan.HasValue)
				return (30002 + brelan.Value, "brelan " + HandQualifiers_de[brelan.Value]);
			if (paires.Count >= 2)
			{
				int p1 = paires[0].Key, p2 = paires[1].Key;
				return (20202 + p1 * 100 + p2, "double paire " + HandQualifiers_de[p1] + " et " + HandQualifiers_de[p2]);
			}
			if (paires.Count == 1)
				return (10002 + paires[0].Key, "paire " + HandQualifiers_de[paires[0].Key]);
			var rang = orderedLookup[0].Key;
			return (2 + rang, FullNames[rang]);
		}

		public static int CardsForce(IEnumerable<Card> cards)
		{
			int force = 0;
			foreach (var card in cards.Select(c => c._value).OrderByDescending(c => c).Take(5))
				force = force * 100 + card + 2;
			return force;
		}
	}
}
