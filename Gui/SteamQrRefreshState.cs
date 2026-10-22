using System;

namespace DotaArchive
{
    // Authorization lifecycle is independent of coalesced UI progress. Every
    // observation belongs to exactly one engine instance; old callbacks cannot
    // authorize a refresh or restore a QR after the next engine has begun.
    internal sealed class SteamQrRefreshState
    {
        private readonly object sync = new object();
        private object currentOwner;
        private bool waiting, retryAllowed, pending;

        internal void Begin(object owner)
        {
            if(owner == null) throw new ArgumentNullException("owner");
            lock(sync) { currentOwner = owner; waiting = false; retryAllowed = false; pending = false; }
        }

        // Returns whether this event belongs to the current engine, including
        // phases that do not change authorization state.
        internal bool Observe(object owner, string phase)
        {
            lock(sync) {
                if(!Current(owner)) return false;
                string normalized = phase ?? "";
                if(normalized.StartsWith("manifest_", StringComparison.Ordinal)) normalized = normalized.Substring(9);
                switch(normalized) {
                    case "authentication": waiting = true; retryAllowed = true; break;
                    case "authenticated":
                    case "downloading":
                    case "preparing":
                    case "downloaded":
                    case "manifests_downloaded":
                    case "preparing_manifests":
                    case "manifests_prepared":
                    case "verifying":
                    case "verified":
                    case "completed":
                    case "文件校验":
                        waiting = false; retryAllowed = false; break;
                    case "error":
                    case "connecting":
                        waiting = false; break;
                }
                return true;
            }
        }

        internal bool CanRefresh(bool busy, bool complete)
        {
            lock(sync) return CanRefreshCore(busy, complete);
        }

        internal bool Request(bool busy, bool complete)
        {
            lock(sync) {
                if(!CanRefreshCore(busy, complete)) return false;
                pending = true; return true;
            }
        }

        internal bool IsCurrent(object owner) { lock(sync) return Current(owner); }
        internal bool CanShowQr(object owner) { lock(sync) return Current(owner) && waiting && !pending; }
        internal bool Pending { get { lock(sync) return pending; } }
        internal bool Waiting { get { lock(sync) return waiting; } }
        internal bool RetryAllowed { get { lock(sync) return retryAllowed; } }
        internal bool TakePending() { lock(sync) { bool value = pending; pending = false; return value; } }
        internal void CancelPending() { lock(sync) pending = false; }

        internal void End(object owner)
        {
            lock(sync) {
                if(!Current(owner)) return;
                currentOwner = null; waiting = false;
            }
        }

        private bool Current(object owner) { return owner != null && Object.ReferenceEquals(owner, currentOwner); }
        private bool CanRefreshCore(bool busy, bool complete) { return complete && !pending && (busy ? waiting : retryAllowed); }
    }
}
