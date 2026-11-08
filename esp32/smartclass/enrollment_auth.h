#pragma once

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <mbedtls/md.h>
#include <mbedtls/platform_util.h>

namespace EnrollmentAuth {
constexpr size_t KeySize = 32;
constexpr size_t ChallengeSize = 32;
constexpr size_t TagSize = 32;
constexpr size_t MaxBodySize = 4096;

inline void toHex(const uint8_t* input, size_t length, char* output) {
  const char digits[] = "0123456789abcdef";
  for (size_t i = 0; i < length; ++i) {
    output[i * 2] = digits[input[i] >> 4];
    output[i * 2 + 1] = digits[input[i] & 15];
  }
  output[length * 2] = '\0';
}

inline bool computeTag(const uint8_t* key, size_t keyLength, bool response,
                       const char* deviceId, const char* operation,
                       const uint8_t* challenge, size_t challengeLength,
                       const uint8_t* body, size_t bodyLength, uint8_t* tag) {
  if (!key || keyLength != KeySize || !challenge || challengeLength != ChallengeSize ||
      !deviceId || !operation || !tag || (!body && bodyLength != 0) || bodyLength > MaxBodySize) {
    return false;
  }
  size_t deviceLength = strlen(deviceId);
  size_t operationLength = strlen(operation);
  if (deviceLength == 0 || deviceLength > 23 || operationLength == 0 || operationLength > 32) return false;
  for (size_t i = 0; i < deviceLength; ++i) {
    if (deviceId[i] < '0' || deviceId[i] > '9') return false;
  }
  for (size_t i = 0; i < operationLength; ++i) {
    char ch = operation[i];
    if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_')) return false;
  }
  const mbedtls_md_info_t* sha = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
  if (!sha) return false;
  uint8_t digest[32];
  const uint8_t* input = body ? body : reinterpret_cast<const uint8_t*>("");
  if (mbedtls_md(sha, input, bodyLength, digest) != 0) return false;
  char challengeHex[65], digestHex[65], canonical[256];
  toHex(challenge, ChallengeSize, challengeHex);
  toHex(digest, sizeof(digest), digestHex);
  int length = snprintf(canonical, sizeof(canonical), "SmartClassAC-enroll-v1\n%s\n%s\n%s\n%s\n%s\n",
                        response ? "response" : "request", deviceId, operation, challengeHex, digestHex);
  bool ok = length > 0 && static_cast<size_t>(length) < sizeof(canonical) &&
            mbedtls_md_hmac(sha, key, keyLength, reinterpret_cast<const uint8_t*>(canonical),
                            static_cast<size_t>(length), tag) == 0;
  mbedtls_platform_zeroize(digest, sizeof(digest));
  mbedtls_platform_zeroize(canonical, sizeof(canonical));
  return ok;
}

inline bool verifyTag(const uint8_t* key, size_t keyLength, bool response,
                      const char* deviceId, const char* operation,
                      const uint8_t* challenge, size_t challengeLength,
                      const uint8_t* body, size_t bodyLength, const uint8_t* tag, size_t tagLength) {
  if (!tag || tagLength != TagSize) return false;
  uint8_t expected[TagSize];
  if (!computeTag(key, keyLength, response, deviceId, operation, challenge, challengeLength, body, bodyLength, expected)) return false;
  uint8_t difference = 0;
  for (size_t i = 0; i < TagSize; ++i) difference |= expected[i] ^ tag[i];
  mbedtls_platform_zeroize(expected, sizeof(expected));
  return difference == 0;
}
}
