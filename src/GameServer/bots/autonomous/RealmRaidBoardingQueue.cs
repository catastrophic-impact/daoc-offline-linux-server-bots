using System;
using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>One bounded departure lane per expedition stablemaster, without another timer.</summary>
    public sealed class RealmRaidBoardingQueue
    {
        private sealed class Request
        {
            public WeakReference<object> Rider;
            public long Seen, Admitted;
        }
        private readonly List<Request> _waiting = new();
        private long _nextDeparture;

        public bool TryEnter(object rider, long now)
        {
            if (rider == null) return false;
            lock (_waiting)
            {
                // A dead/cancelled/stuck rider cannot own the NPC indefinitely.
                // The admission lease does not renew while walking towards it.
                _waiting.RemoveAll(r => !r.Rider.TryGetTarget(out _) ||
                    now - r.Seen >= 15_000 || r.Admitted != 0 && now - r.Admitted >= 15_000);
                Request request = _waiting.Find(r => r.Rider.TryGetTarget(out var owner) && ReferenceEquals(owner, rider));
                if (request == null)
                {
                    if (_waiting.Count >= RealmRaidRecruitmentPolicy.MaximumParties) return false;
                    request = new() { Rider = new(rider), Seen = now };
                    _waiting.Add(request);
                }
                request.Seen = now;
                if (!ReferenceEquals(_waiting[0], request) || now < _nextDeparture) return false;
                if (request.Admitted == 0) request.Admitted = Math.Max(1, now);
                return true;
            }
        }

        public void Leave(object rider, long now)
        {
            lock (_waiting)
            {
                bool heldLane = _waiting.Count > 0 && _waiting[0].Admitted != 0 &&
                    _waiting[0].Rider.TryGetTarget(out var owner) && ReferenceEquals(owner, rider);
                _waiting.RemoveAll(r => r.Rider.TryGetTarget(out var actor) && ReferenceEquals(actor, rider));
                if (heldLane) _nextDeparture = now + 750;
            }
        }
    }
}
