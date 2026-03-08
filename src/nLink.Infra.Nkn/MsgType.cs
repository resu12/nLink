namespace NLink.Infra.Nkn;

internal enum MsgType
{
    Presence = 0,
    JoinRequest = 1,
    Approve = 2,
    Reject = 3,
    Chat = 4,
    Ack = 5,
    SessionEnd = 6,
    ControlRequest = 7,
    ControlResponse = 8,
    ControlStart = 9,
    ControlStop = 10,
    ControlInput = 11,
    ControlDisplayInfo = 12,
    ControlAck = 13,
    ControlStateSnapshot = 14,
    SessionHandshakeStart = 15,
    SessionHandshakeChallenge = 16,
    SessionHandshakeResponse = 17,
    SessionHandshakeResult = 18,
    ScreenShareStop = 19,
}
