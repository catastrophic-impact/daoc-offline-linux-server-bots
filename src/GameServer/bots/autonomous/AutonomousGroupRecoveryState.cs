using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>At most eight members. No world scans, movement, timers or persistence.</summary>
    public sealed class AutonomousGroupRecoveryState
    {
        public readonly record struct Member(long Id, int Deaths, bool Alive, bool Returning = false,
            bool Riding = false, bool AtRendezvous = true, bool Busy = false, bool ResourcesReady = true);
        public const long ReadySettleMilliseconds = 5_000;
        private readonly Dictionary<long, int> _deaths = new();
        private long? _readySince;
        public bool IsRegrouping { get; private set; }

        public bool HasCasualty(IEnumerable<Member> members) => members.Any(member =>
            !member.Alive || member.Returning ||
            _deaths.TryGetValue(member.Id, out int previous) && member.Deaths > previous);

        public bool Observe(Member[] members, bool taskStarted)
        {
            bool casualty = HasCasualty(members);
            bool start = taskStarted && !IsRegrouping && casualty;
            if (IsRegrouping && casualty) _readySince = null;
            foreach (Member member in members) _deaths[member.Id] = member.Deaths;
            if (start) { IsRegrouping = true; _readySince = null; }
            return start;
        }

        /// <summary>A nearby resurrection needs resource recovery, not a trip
        /// through the town rendezvous. Absorb that death only while the whole
        /// living party is together; a released or distant member still starts
        /// the ordinary regroup episode.</summary>
        public bool TryResumeLocally(Member[] members, bool together)
        {
            if (IsRegrouping || !together || members == null || members.Length < 2 ||
                members.Any(member => !member.Alive || member.Returning || member.Riding))
                return false;
            foreach (Member member in members)
                _deaths[member.Id] = member.Deaths;
            return true;
        }

        public bool TryComplete(Member[] members, long now)
        {
            if (!IsRegrouping) return false;
            bool ready = members.Length >= 2 && members.All(member => member.Alive && !member.Returning &&
                !member.Riding && member.AtRendezvous && !member.Busy && member.ResourcesReady);
            if (!ready) { _readySince = null; return false; }
            _readySince ??= now;
            if (now - _readySince.Value < ReadySettleMilliseconds) return false;
            IsRegrouping = false;
            _readySince = null;
            return true;
        }

        public static bool ResourcesReady(int health, int power, int endurance, bool usesPower) =>
            health >= 90 && (!usesPower || power >= 80) && endurance >= 80;
    }
}
