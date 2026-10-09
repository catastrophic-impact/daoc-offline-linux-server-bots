using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>
    /// Healers and buffers walk to a group member who is out of spell range, but only within
    /// 2,500 units in the same region. If the walk gets no 100 units closer for 12 seconds
    /// (the member is unreachable or keeps moving away) that member is skipped for 30 seconds,
    /// so a support bot never spends every turn walking toward someone it cannot reach.
    /// </summary>
    public sealed class BotSupportReach
    {
        public const int SupportRadius = 2500;
        public const long GiveUpMilliseconds = 12_000;
        public const long SkipMilliseconds = 30_000;
        public const float ProgressUnits = 100f;

        private object _target;
        private long _since;
        private float _best;
        private readonly Dictionary<object, long> _skipped = new();

        public bool IsSkipped(object target, long now)
        {
            if (target == null || !_skipped.TryGetValue(target, out long until))
                return false;
            if (until > now)
                return true;
            _skipped.Remove(target);
            return false;
        }

        /// <summary>True to keep walking toward this target; false once it is skipped.</summary>
        public bool KeepApproaching(object target, float distance, long now)
        {
            if (!ReferenceEquals(_target, target))
            {
                _target = target;
                _since = now;
                _best = distance;
                return true;
            }
            if (distance < _best - ProgressUnits)
            {
                _best = distance;
                _since = now;
                return true;
            }
            if (now - _since < GiveUpMilliseconds)
                return true;
            _skipped[target] = now + SkipMilliseconds;
            _target = null;
            return false;
        }

        public void Reached(object target)
        {
            if (ReferenceEquals(_target, target))
                _target = null;
        }
    }
}
