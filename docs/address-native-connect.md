# Address-Native Session Connect (P7.0)

## Summary

nLink now uses **direct NKN peer address** as canonical identity.

- `PeerAddress` is the only connect target.
- `InviteToken` is a signed, expiring wrapper around connect metadata.
- Helper connect resolution is local and deterministic:
  - raw address input, or
  - validated invite token input.

Legacy short-code identity indirection is removed from the active product flow.

## Why Short-Code Connect Was Deprecated

The old short-code model depended on lookup/discovery indirection that was not required for direct peer-to-peer sessions and increased attack surface and ambiguity.

Address-native connect reduces risk and complexity by:

- removing code-lookup dependency from primary flow,
- using explicit signed metadata for invite-based connect,
- making connect target auditable (`PeerAddress`) before connect starts.

## Trust Model

- **Parsing** and **validation** are separate steps.
- Invite validation fails with explicit result objects:
  - malformed token,
  - expired token,
  - signature invalid / tampered payload.
- Connect can run without external servers beyond transport itself.

## Runtime Behavior

- Helpee advertises invite/address to helper.
- Helper resolves input to a direct target address.
- Session start uses address-targeted connect path.
- Invalid input is rejected before transport join attempt.
