#pragma once

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

// Verifies a signed server recovery response. Unconnected: a true result is
// not permission to write a sensor slot.
namespace EnrollmentRecovery {
enum class State : uint8_t { Unresolved, Completed };

constexpr size_t NonceBytes = 32;
constexpr size_t NonceHexLength = NonceBytes * 2;

// Fills a fresh 64-character lowercase hex nonce. fill() must return true only
// when it wrote every byte from a hardware random source.
template <typename Fill>
bool makeClientNonce(Fill fill, char* output, size_t outputSize) {
  if (!output || outputSize < NonceHexLength + 1) return false;
  uint8_t random[NonceBytes];
  if (!fill(random, sizeof(random))) return false;
  const char digits[] = "0123456789abcdef";
  for (size_t i = 0; i < NonceBytes; ++i) {
    output[i * 2] = digits[random[i] >> 4];
    output[i * 2 + 1] = digits[random[i] & 15];
  }
  output[NonceHexLength] = '\0';
  memset(random, 0, sizeof(random));
  return true;
}

struct Expected {
  const char* preparation;  // canonical lowercase GUID
  const char* operation;    // canonical lowercase GUID
  uint16_t slot;
  const char* clientNonce;  // 64 lowercase hex characters
};

// The verifier receives the exact body and tag and must return true only for a
// valid MAC over that body. The body is "preparation\noperation\nslot\nnonce\nstate\n".
template <typename Verify>
bool verifyResponse(const Expected& expected, Verify verify,
                    const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength,
                    State& state) {
  if (!expected.preparation || !expected.operation || !expected.clientNonce || expected.slot == 0 ||
      !body || bodyLength == 0 || bodyLength > 256)
    return false;

  char prefix[200];
  int prefixLength = snprintf(prefix, sizeof(prefix), "%s\n%s\n%u\n%s\n",
                              expected.preparation, expected.operation,
                              static_cast<unsigned>(expected.slot), expected.clientNonce);
  if (prefixLength <= 0 || static_cast<size_t>(prefixLength) >= sizeof(prefix)) return false;
  size_t prefixSize = static_cast<size_t>(prefixLength);

  const char* stateText = nullptr;
  State candidate = State::Unresolved;
  if (bodyLength == prefixSize + strlen("unresolved\n") &&
      memcmp(body + prefixSize, "unresolved\n", strlen("unresolved\n")) == 0) {
    stateText = "unresolved\n";
    candidate = State::Unresolved;
  } else if (bodyLength == prefixSize + strlen("completed\n") &&
             memcmp(body + prefixSize, "completed\n", strlen("completed\n")) == 0) {
    stateText = "completed\n";
    candidate = State::Completed;
  }
  if (!stateText || memcmp(body, prefix, prefixSize) != 0) return false;
  if (!verify(body, bodyLength, tag, tagLength)) return false;

  state = candidate;
  return true;
}

// Builds the device-signed request body: preparation, operation, slot and nonce lines.
inline bool buildRequestBody(const char* preparation, const char* operation, uint16_t slot,
                             const char* clientNonce, char* out, size_t outSize, size_t& outLength) {
  if (!preparation || !operation || !clientNonce || slot == 0 || !out) return false;
  int length = snprintf(out, outSize, "%s\n%s\n%u\n%s\n", preparation, operation,
                        static_cast<unsigned>(slot), clientNonce);
  if (length <= 0 || static_cast<size_t>(length) >= outSize) return false;
  outLength = static_cast<size_t>(length);
  return true;
}

// Decodes a JSON string body, where each newline arrives as a \n escape.
// Any other escape or control character is rejected.
inline bool decodeJsonBody(const char* escaped, size_t escapedLength, char* out, size_t outSize, size_t& outLength) {
  if (!escaped || !out) return false;
  size_t n = 0;
  for (size_t i = 0; i < escapedLength; ++i) {
    char ch = escaped[i];
    if (ch == '\\') {
      if (i + 1 >= escapedLength || escaped[i + 1] != 'n') return false;
      ch = '\n';
      ++i;
    } else if (static_cast<unsigned char>(ch) < 0x20 || ch == '"') {
      return false;
    }
    if (n + 1 >= outSize) return false;
    out[n++] = ch;
  }
  out[n] = '\0';
  outLength = n;
  return true;
}

// Decodes exactly outLength bytes from lowercase hex (the server's encoding).
inline bool decodeHex(const char* hex, size_t hexLength, uint8_t* out, size_t outLength) {
  if (!hex || !out || hexLength != outLength * 2) return false;
  for (size_t i = 0; i < outLength; ++i) {
    uint8_t value = 0;
    for (size_t j = 0; j < 2; ++j) {
      char ch = hex[i * 2 + j];
      uint8_t digit;
      if (ch >= '0' && ch <= '9') digit = static_cast<uint8_t>(ch - '0');
      else if (ch >= 'a' && ch <= 'f') digit = static_cast<uint8_t>(ch - 'a' + 10);
      else return false;
      value = static_cast<uint8_t>((value << 4) | digit);
    }
    out[i] = value;
  }
  return true;
}
}
