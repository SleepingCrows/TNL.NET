﻿using System;

namespace TNL.NET.Entities
{
    using Data;
    using Notify;
    using Types;
    using Utils;

    public class EventNote
    {
        public NetEvent Event { get; set; }
        public Int32 SeqCount { get; set; }
        public EventNote NextEvent { get; set; }
    }

    public class EventConnection : NetConnection
    {
        public const UInt32 DebugCheckSum = 0xF00DBAADU;
        public const Byte BitStreamPosBitSize = 16;
        public const Int32 InvalidSendEventSeq = -1;
        public const Int32 FirstValidSendEventSeq = 0;

        private EventNote _sendEventQueueHead;
        private EventNote _sendEventQueueTail;
        private EventNote _unorderedSendEventQueueHead;
        private EventNote _unorderedSendEventQueueTail;
        private EventNote _waitSeqEvents;
        private EventNote _notifyEventList;

        private Int32 _nextSendEventSeq;
        private Int32 _nextRecvEventSeq;
        private Int32 _lastAckedEventSeq;
        private readonly Single _packetFillFraction;

        protected UInt32 EventClassCount;
        protected UInt32 EventClassBitSize;
        protected UInt32 EventClassVersion;

        public UInt32 NumEventsWaiting { get; set; }

        public EventConnection()
        {
            _notifyEventList = null;
            _sendEventQueueHead = null;
            _sendEventQueueTail = null;
            _unorderedSendEventQueueHead = null;
            _unorderedSendEventQueueTail = null;
            _waitSeqEvents = null;

            _nextSendEventSeq = FirstValidSendEventSeq;
            _nextRecvEventSeq = FirstValidSendEventSeq;
            _lastAckedEventSeq = -1;

            EventClassCount = 0;
            EventClassBitSize = 0;

            NumEventsWaiting = 0;

            _packetFillFraction = 1.0f;
        }

        ~EventConnection()
        {
            while (_notifyEventList != null)
            {
                var temp = _notifyEventList;
                _notifyEventList = temp.NextEvent;

                temp.Event.NotifyDelivered(this, true);
            }

            while (_unorderedSendEventQueueHead != null)
            {
                var temp = _unorderedSendEventQueueHead;
                _unorderedSendEventQueueHead = temp.NextEvent;

                temp.Event.NotifyDelivered(this, true);
            }

            while (_sendEventQueueHead != null)
            {
                var temp = _sendEventQueueHead;
                _sendEventQueueHead = temp.NextEvent;

                temp.Event.NotifyDelivered(this, true);
            }
        }

        protected override PacketNotify AllocNotify()
        {
            return new EventPacketNotify();
        }

        protected override void PacketDropped(PacketNotify note)
        {
            base.PacketDropped(note);

            var notify = note as EventPacketNotify;
            if (notify == null)
                return;

            var walk = notify.EventList;
            var insertList = _sendEventQueueHead;

            while (walk != null)
            {
                switch (walk.Event.GuaranteeType)
                {
                    case GuaranteeType.GuaranteedOrdered:
                        Console.WriteLine("EventConnection {0}: DroppedGuaranteed - {1}", GetNetAddressString(), walk.SeqCount);

                        EventNote insertListPrev = null;

                        while (insertList != null && insertList.SeqCount < walk.SeqCount)
                        {
                            insertListPrev = insertList;
                            insertList = insertList.NextEvent;
                        }

                        if (insertList == null || insertListPrev == null)
                            continue;

                        var temp = walk.NextEvent;
                        walk.NextEvent = insertList.NextEvent;
                        if (walk.NextEvent != null)
                            _sendEventQueueTail = walk;

                        insertListPrev.NextEvent = walk;

                        insertList = walk.NextEvent;
                        walk = temp;
                        break;

                    case GuaranteeType.Guaranteed:
                        var temp1 = walk.NextEvent;
                        walk.NextEvent = _unorderedSendEventQueueHead;
                        _unorderedSendEventQueueHead = walk;
                        if (walk.NextEvent != null)
                            _unorderedSendEventQueueTail = walk;

                        walk = temp1;
                        break;

                    case GuaranteeType.Unguaranteed:
                        walk.Event.NotifyDelivered(this, false);
                        var temp2 = walk.NextEvent;
                        walk = temp2;
                        break;
                }

                ++NumEventsWaiting;
            }
        }

        protected override void PacketReceived(PacketNotify note)
        {
            base.PacketReceived(note);

            var notify = note as EventPacketNotify;
            if (notify == null)
                return;

            var walk = notify.EventList;
            var noteList = _notifyEventList;
            var noteListPrev = _notifyEventList;

            while (walk != null)
            {
                var next = walk.NextEvent;
                if (walk.Event.GuaranteeType != GuaranteeType.GuaranteedOrdered)
                {
                    walk.Event.NotifyDelivered(this, true);
                    walk = next;
                }
                else
                {
                    if (noteList == null)
                        _notifyEventList = walk;
                    else
                    {
                        while (noteList != null && noteList.SeqCount < walk.SeqCount)
                        {
                            noteListPrev = noteList;
                            noteList = noteList.NextEvent;
                        }

                        walk.NextEvent = noteList;
                        noteListPrev.NextEvent = walk;
                    }

                    noteList = walk;
                    noteListPrev = walk;

                    walk = next;
                }
            }

            while (_notifyEventList != null && _notifyEventList.SeqCount == _lastAckedEventSeq + 1)
            {
                ++_lastAckedEventSeq;
                var next = _notifyEventList.NextEvent;

                Console.WriteLine("EventConnection {0}: NotifyDelivered - {1}", GetNetAddressString(), _notifyEventList.SeqCount);

                _notifyEventList.Event.NotifyDelivered(this, true);

                _notifyEventList = next;
            }
        }

        protected override void WritePacket(BitStream stream, PacketNotify note)
        {
            base.WritePacket(stream, note);

            var notify = note as EventPacketNotify;
            if (notify == null)
                return;

            if (ConnectionParameters.DebugObjectSizes)
                stream.WriteInt(DebugCheckSum, 32);

            EventNote packQueueHead = null, packQueueTail = null;

            var totalPacketSpaceFraction = 1.0f / stream.MaxWriteBitNum;

            while (_unorderedSendEventQueueHead != null)
            {
                if (stream.IsFull() || (stream.GetBitPosition() * totalPacketSpaceFraction) > _packetFillFraction)
                    break;

                var ev = _unorderedSendEventQueueHead;
                stream.WriteFlag(true);

                var start = stream.GetBitPosition();

                if (ConnectionParameters.DebugObjectSizes)
                    stream.AdvanceBitPosition(BitStreamPosBitSize);

                var classId = ev.Event.GetClassId(GetNetClassGroup());
                stream.WriteInt(classId, (Byte) EventClassBitSize);

                ev.Event.Pack(this, stream);

                if (ConnectionParameters.DebugObjectSizes)
                    stream.WriteIntAt(stream.GetBitPosition(), BitStreamPosBitSize, start);

                if (stream.GetBitSpaceAvailable() < MinimumPaddingBits)
                {
                    stream.SetBitPosition(start - 1);
                    stream.ClearError();
                    break;
                }

                --NumEventsWaiting;

                _unorderedSendEventQueueHead = ev.NextEvent;
                ev.NextEvent = null;

                if (packQueueHead == null)
                    packQueueHead = ev;
                else
                    packQueueTail.NextEvent = ev;

                packQueueTail = ev;
            }

            stream.WriteFlag(false);
            const Int32 prevSeq = -2;

            while (_sendEventQueueHead != null)
            {
                if (stream.IsFull())
                    break;

                if (_sendEventQueueHead.SeqCount > _lastAckedEventSeq + 126)
                    break;

                var ev = _sendEventQueueHead;
                var eventStart = stream.GetBitPosition();

                stream.WriteFlag(false);

                if (!stream.WriteFlag(ev.SeqCount == prevSeq + 1))
                    stream.WriteInt((UInt32) ev.SeqCount, 7);

                var start = stream.GetBitPosition();

                var classId = ev.Event.GetClassId(GetNetClassGroup());

                stream.WriteInt(classId, (Byte) EventClassBitSize);

                ev.Event.Pack(this, stream);

                ev.Event.GetClassRep().AddInitialUpdate(stream.GetBitPosition() - start);

                if (ConnectionParameters.DebugObjectSizes)
                    stream.WriteIntAt(stream.GetBitPosition(), BitStreamPosBitSize, start - BitStreamPosBitSize);

                if (stream.GetBitSpaceAvailable() < MinimumPaddingBits)
                {
                    stream.SetBitPosition(eventStart);
                    stream.ClearError();
                    break;
                }

                --NumEventsWaiting;

                _sendEventQueueHead = ev.NextEvent;
                ev.NextEvent = null;

                if (packQueueHead == null)
                    packQueueHead = ev;
                else
                    packQueueTail.NextEvent = ev;

                packQueueTail = ev;
            }

            for (var ev = packQueueHead; ev != null; ev = ev.NextEvent)
                ev.Event.NotifySent(this);

            notify.EventList = packQueueHead;
            stream.WriteFlag(false);
        }

        protected override void ReadPacket(BitStream stream)
        {
            base.ReadPacket(stream);

            if (ConnectionParameters.DebugObjectSizes)
                Console.WriteLine("{0:X8} == {1:X8}", stream.ReadInt(32), DebugCheckSum);

            var prevSeq = -2;
            var waitInsert = _waitSeqEvents;
            var waitInsertPrev = _waitSeqEvents;
            var ungaranteedPhase = true;

            while (true)
            {
                var bit = stream.ReadFlag();
                if (ungaranteedPhase && !bit)
                {
                    ungaranteedPhase = false;
                    bit = stream.ReadFlag();
                }

                if (!ungaranteedPhase && !bit)
                    break;

                var seq = -1;

                if (!ungaranteedPhase)
                {
                    if (stream.ReadFlag())
                        seq = (prevSeq + 1) & 0x7;
                    else
                        seq = (Int32) stream.ReadInt(7);

                    prevSeq = seq;
                }

                UInt32 endingPosition = 0;
                if (ConnectionParameters.DebugObjectSizes)
                    endingPosition = stream.ReadInt(BitStreamPosBitSize);

                var classId = stream.ReadInt((Byte) EventClassBitSize);
                if (classId >= EventClassCount)
                {
                    SetLastError("Invalid packet.");
                    return;
                }

                var evt = (NetEvent) Create((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent, (Int32) classId);
                if (evt == null)
                {
                    SetLastError("Invalid packet.");
                    return;
                }

                if (evt.GetEventDirection() == EventDirection.DirUnset ||
                    (evt.GetEventDirection() == EventDirection.DirServerToClient && IsConnectionToClient()) ||
                    (evt.GetEventDirection() == EventDirection.DirClientToServer && IsConnectionToServer()))
                {
                    SetLastError("Invalid packet.");
                    return;
                }

                evt.Unpack(this, stream);
                if (ErrorBuffer[0] != 0)
                    return;

                if (ConnectionParameters.DebugObjectSizes)
                    Console.WriteLine("Assert({0:X8} == {1:X8}) || unpack did not match pack for event of class {2}.", endingPosition, stream.GetBitPosition(), evt.GetClassName());

                if (ungaranteedPhase)
                {
                    ProcessEvent(evt);

                    if (ErrorBuffer[0] != 0)
                        return;

                    continue;
                }

                seq |= (_nextRecvEventSeq & ~0x7F);
                if (seq < _nextRecvEventSeq)
                    seq += 128;

                var note = new EventNote
                {
                    Event = evt,
                    SeqCount = seq,
                    NextEvent = null
                };

                if (waitInsert == null)
                    _waitSeqEvents = note;
                else
                {
                    while (waitInsert != null && waitInsert.SeqCount < seq)
                    {
                        waitInsertPrev = waitInsert;
                        waitInsert = waitInsert.NextEvent;
                    }

                    note.NextEvent = waitInsert;
                    waitInsertPrev.NextEvent = note;
                }

                waitInsertPrev = note;
                waitInsert = note;
            }

            while (_waitSeqEvents != null && _waitSeqEvents.SeqCount == _nextRecvEventSeq)
            {
                ++_nextRecvEventSeq;

                var temp = _waitSeqEvents;
                _waitSeqEvents = temp.NextEvent;

                ProcessEvent(temp.Event);

                if (ErrorBuffer[0] != 0)
                    return;
            }
        }

        public override Boolean IsDataToTransmit()
        {
            return _unorderedSendEventQueueHead != null || _sendEventQueueHead != null || base.IsDataToTransmit();
        }

        public void ProcessEvent(NetEvent theEvent)
        {
            if (GetConnectionState() == NetConnectionState.Connected)
                theEvent.Process(this);
        }

        public override void WriteConnectRequest(BitStream stream)
        {
            base.WriteConnectRequest(stream);

            stream.Write(NetClassRep.GetNetClassCount((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent));
        }

        public override Boolean ReadConnectRequest(BitStream stream, ref String errorString)
        {
            if (!base.ReadConnectRequest(stream, ref errorString))
                return false;

            UInt32 classCount;
            stream.Read(out classCount);

            var myCount = NetClassRep.GetNetClassCount((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent);
            if (myCount <= classCount)
                EventClassCount = myCount;
            else
            {
                EventClassCount = classCount;
                if (!NetClassRep.IsVersionBorderCount((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent, EventClassVersion))
                    return false;
            }

            EventClassVersion = (UInt32) NetClassRep.GetClass((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent, EventClassCount - 1).ClassVersion;
            EventClassBitSize = Utils.GetNextBinLog2(EventClassCount);
            return true;
        }

        public override void WriteConnectAccept(BitStream stream)
        {
            base.WriteConnectAccept(stream);

            stream.Write(EventClassCount);
        }

        public override bool ReadConnectAccept(BitStream stream, ref String errorString)
        {
            if (!base.ReadConnectAccept(stream, ref errorString))
                return false;

            stream.Read(out EventClassCount);
            var myCount = NetClassRep.GetNetClassCount((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent);

            if (EventClassCount > myCount)
                return false;

            if (!NetClassRep.IsVersionBorderCount((UInt32) GetNetClassGroup(), (UInt32) NetClassType.NetClassTypeEvent, EventClassCount))
                return false;

            EventClassBitSize = Utils.GetNextBinLog2(EventClassCount);
            return true;
        }

        public UInt32 GetEventClassVersion()
        {
            return EventClassVersion;
        }

        public Boolean PostNetEvent(NetEvent theEvent)
        {
            var classId = theEvent.GetClassId(GetNetClassGroup());
            if (classId >= EventClassCount && GetConnectionState() == NetConnectionState.Connected)
                return false;

            var ev = new EventNote
            {
                Event = theEvent,
                NextEvent = null
            };

            if (ev.Event.GuaranteeType == GuaranteeType.GuaranteedOrdered)
            {
                ev.SeqCount = _nextSendEventSeq++;

                if (_sendEventQueueHead == null)
                    _sendEventQueueHead = ev;
                else
                    _sendEventQueueTail.NextEvent = ev;

                _sendEventQueueTail = ev;
            }
            else
            {
                ev.SeqCount = InvalidSendEventSeq;

                if (_unorderedSendEventQueueHead == null)
                    _unorderedSendEventQueueHead = ev;
                else
                    _unorderedSendEventQueueTail.NextEvent = ev;

                _unorderedSendEventQueueTail = ev;
            }

            ++NumEventsWaiting;
            return true;
        }
    }
}