using System;
using LiteNetLib;

namespace LiteEntitySystem.Transport
{
    public class LiteNetLibNetPeer : IAbstractNetPeer
    {
        public readonly NetPeer NetPeer;
        protected NetPlayer NetPlayer;
        NetPlayer IAbstractNetPeer.AssignedPlayer { get => NetPlayer; set => NetPlayer = value; }
        public int RoundTripTimeMs => NetPeer.RoundTripTime;

        public LiteNetLibNetPeer(NetPeer netPeer, bool assignToTag)
        {
            NetPeer = netPeer;
            if(assignToTag)
                NetPeer.Tag = this;
        }

        public void TriggerSend() => NetPeer.NetManager.TriggerUpdate();
        public void SendReliableOrdered(ReadOnlySpan<byte> data) => NetPeer.Send(data, 0, DeliveryMethod.ReliableOrdered);
        public void SendUnreliable(ReadOnlySpan<byte> data) => NetPeer.Send(data, 0, DeliveryMethod.Unreliable);
        public int GetMaxUnreliablePacketSize() => NetPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
        public override string ToString() => NetPeer.ToString();
    }

    public static class LiteNetLibExtensions
    {
        public static LiteNetLibNetPeer GetLiteNetLibNetPeerFromTag(this NetPeer peer) => (LiteNetLibNetPeer)peer.Tag;
        public static LiteNetLibNetPeer GetLiteNetLibNetPeer(this NetPlayer player) => (LiteNetLibNetPeer)player.Peer;
    }
}