#pragma once

#include "enrollment_journal.h"
#include "enrollment_recovery.h"

namespace ReplacementOperation {
inline bool parseId(const uint8_t* input, uint8_t* output) {
  size_t byte = 0;
  uint8_t high = 0;
  bool haveHigh = false;
  for (size_t i = 0; i < 36; ++i) {
    char ch = static_cast<char>(input[i]);
    if (i == 8 || i == 13 || i == 18 || i == 23) {
      if (ch != '-') return false;
      continue;
    }
    uint8_t digit;
    if (ch >= '0' && ch <= '9') digit = static_cast<uint8_t>(ch - '0');
    else if (ch >= 'a' && ch <= 'f') digit = static_cast<uint8_t>(ch - 'a' + 10);
    else return false;
    if (!haveHigh) { high = digit; haveHigh = true; }
    else { output[byte++] = static_cast<uint8_t>((high << 4) | digit); haveHigh = false; }
  }
  return byte == EnrollmentJournal::IdSize && !haveHigh && EnrollmentJournal::validId(output);
}

template <typename Verify>
bool verifyResponse(const char* nonce, Verify verify, const uint8_t* body, size_t bodyLength,
                    const uint8_t* tag, size_t tagLength, EnrollmentJournal::Record& operation) {
  constexpr size_t prefix = 74;
  constexpr size_t suffix = 1 + EnrollmentRecovery::NonceHexLength + sizeof("\nreplacement\n") - 1;
  if (!nonce || strlen(nonce) != EnrollmentRecovery::NonceHexLength || !body || !tag ||
      tagLength != 32 || bodyLength < prefix + 1 + suffix || bodyLength > prefix + 5 + suffix ||
      body[36] != '\n' || body[73] != '\n') return false;
  for (size_t i = 0; i < EnrollmentRecovery::NonceHexLength; ++i)
    if (!((nonce[i] >= '0' && nonce[i] <= '9') || (nonce[i] >= 'a' && nonce[i] <= 'f'))) return false;
  size_t slotLength = bodyLength - prefix - suffix;
  if (body[prefix] == '0' || body[prefix + slotLength] != '\n' ||
      memcmp(body + prefix + slotLength + 1, nonce, EnrollmentRecovery::NonceHexLength) != 0 ||
      memcmp(body + prefix + slotLength + 1 + EnrollmentRecovery::NonceHexLength,
             "\nreplacement\n", sizeof("\nreplacement\n") - 1) != 0) return false;
  uint32_t slot = 0;
  for (size_t i = 0; i < slotLength; ++i) {
    uint8_t ch = body[prefix + i];
    if (ch < '0' || ch > '9') return false;
    slot = slot * 10 + ch - '0';
  }
  if (slot == 0 || slot > UINT16_MAX) return false;
  EnrollmentJournal::Record candidate{};
  if (!parseId(body, candidate.preparation) || !parseId(body + 37, candidate.operation)) return false;
  candidate.slot = static_cast<uint16_t>(slot);
  candidate.kind = EnrollmentJournal::Kind::Replacement;
  candidate.state = EnrollmentJournal::State::ReplacementPrepared;
  if (!verify(body, bodyLength, tag, tagLength)) return false;
  operation = candidate;
  return true;
}

// Discovery does not reserve a guard or grant a sensor write.
template <typename Adapter>
bool fetch(Adapter& adapter, EnrollmentJournal::Record& operation) {
  char nonce[EnrollmentRecovery::NonceHexLength + 1] = {}, request[EnrollmentRecovery::NonceHexLength + 2];
  uint8_t challenge[32], requestTag[32], responseTag[32], response[256];
  size_t responseLength = 0;
  if (!adapter.getChallenge(challenge) || !adapter.makeNonce(nonce) ||
      nonce[EnrollmentRecovery::NonceHexLength] != '\0') return false;
  if (strlen(nonce) != EnrollmentRecovery::NonceHexLength) return false;
  memcpy(request, nonce, EnrollmentRecovery::NonceHexLength);
  request[EnrollmentRecovery::NonceHexLength] = '\n';
  request[EnrollmentRecovery::NonceHexLength + 1] = '\0';
  if (!adapter.sign(challenge, reinterpret_cast<const uint8_t*>(request), sizeof(request) - 1, requestTag) ||
      !adapter.exchange(nonce, challenge, requestTag, response, sizeof(response), responseLength, responseTag) ||
      responseLength > sizeof(response)) return false;
  return verifyResponse(nonce,
      [&](const uint8_t* body, size_t length, const uint8_t* tag, size_t tagLength) {
        return adapter.verify(challenge, body, length, tag, tagLength);
      }, response, responseLength, responseTag, sizeof(responseTag), operation);
}
}
