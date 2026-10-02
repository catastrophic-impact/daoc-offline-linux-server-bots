using System;
using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>
    /// Simple random number deck for combat calculations.
    /// Provides a shuffled deck of values to avoid streaky randomness.
    /// </summary>
    public class PlayerDeck
    {
        private readonly List<int> _deck = new List<int>();
        private int _index;
        private readonly Random _random = new Random();

        public PlayerDeck()
        {
            Reset();
        }

        public void Reset()
        {
            _deck.Clear();
            for (int i = 0; i < 100; i++)
                _deck.Add(i);
            Shuffle();
        }

        private void Shuffle()
        {
            for (int i = _deck.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_deck[i], _deck[j]) = (_deck[j], _deck[i]);
            }
            _index = 0;
        }

        public int GetInt(int max)
        {
            if (_index >= _deck.Count)
                Shuffle();
            int val = _deck[_index++];
            return val % max;
        }

        public double GetDouble()
        {
            if (_index >= _deck.Count)
                Shuffle();
            return _deck[_index++] / 100.0;
        }
    }
}
