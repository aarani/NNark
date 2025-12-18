using NBitcoin.Secp256k1;

namespace NArk.Abstractions.Wallets;

public record SignResult(SecpSchnorrSignature Signature, ECXOnlyPubKey PubKey);