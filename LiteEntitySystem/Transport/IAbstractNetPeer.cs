using System;

namespace LiteEntitySystem.Transport {
    public interface IAbstractNetPeer {
        void TriggerSend();
        void SendReliableOrdered(ReadOnlySpan<byte> data);
        void SendUnreliable(ReadOnlySpan<byte> data);
        int GetMaxUnreliablePacketSize();

        NetPlayer AssignedPlayer { get; set; }
        int RoundTripTimeMs { get; }
    }
}