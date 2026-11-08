#pragma once
#include "faculty_menu.h"

namespace AcIr {
constexpr uint32_t PollIntervalMs = 5000;
inline bool pollDue(uint32_t now, uint32_t last) {
  return static_cast<uint32_t>(now - last) >= PollIntervalMs;
}

enum class Reply : uint8_t { Registered, Denied, Ready, Reconcile, InFlight, Step, Power, Completed };
enum class PollOutcome : uint8_t { Registered, BootUnavailable, Idle, Waiting, Sent, Halted };
struct Step {
  char id[33];
  unsigned from;
  unsigned to;
};

template <typename Verify>
bool parse(unsigned operation, const char* digest, Verify verify, const uint8_t* body, size_t length,
           const uint8_t* tag, size_t tagLength, Reply& reply, Step& step, bool powerEnabled = false) {
  if (!digest || strlen(digest) != 64 || !body || !tag || tagLength != 32 || length < 69 ||
      memcmp(body, digest, 64) != 0 || body[64] != '\n' || !verify(body, length, tag, tagLength)) return false;
  const char* p = reinterpret_cast<const char*>(body) + 65;
  const char* end = reinterpret_cast<const char*>(body) + length;
  char status[12];
  if (!FacultyMenu::line(p, end, status, sizeof(status))) return false;
  if (strcmp(status, "denied") == 0 && p == end) { reply = Reply::Denied; return true; }
  if (operation == 0 && strcmp(status, "registered") == 0 && p == end) { reply = Reply::Registered; return true; }
  if (operation == 2 && strcmp(status, "completed") == 0 && p == end) { reply = Reply::Completed; return true; }
  if (operation != 1) return false;
  if (strcmp(status, "ready") == 0 && p == end) { reply = Reply::Ready; return true; }
  if (strcmp(status, "reconcile") == 0 && p == end) { reply = Reply::Reconcile; return true; }
  if (strcmp(status, "inflight") == 0 && p == end) { reply = Reply::InFlight; return true; }
  Step candidate{};
  char from[4], to[4];
  const bool power = strcmp(status, "power") == 0;
  if ((!power && strcmp(status, "step") != 0) || (power && !powerEnabled) ||
      !FacultyMenu::line(p, end, candidate.id, sizeof(candidate.id)) ||
      strlen(candidate.id) != 32 || !FacultyMenu::line(p, end, from, sizeof(from)) ||
      !FacultyMenu::line(p, end, to, sizeof(to)) || p != end ||
      !FacultyMenu::number(from, candidate.from) || !FacultyMenu::number(to, candidate.to) ||
      (power ? (candidate.from > 1 || candidate.to > 1 || candidate.from == candidate.to)
             : (candidate.from < 16 || candidate.from > 30 || candidate.to < 16 || candidate.to > 30 ||
                (candidate.from + 1 != candidate.to && candidate.to + 1 != candidate.from)))) return false;
  for (size_t i = 0; i < 32; ++i)
    if (!((candidate.id[i] >= '0' && candidate.id[i] <= '9') ||
          (candidate.id[i] >= 'a' && candidate.id[i] <= 'f'))) return false;
  step = candidate;
  reply = power ? Reply::Power : Reply::Step;
  return true;
}

template <typename Request, typename Send>
PollOutcome poll(bool& registered, bool& halted, Request request, Send send) {
  if (halted) return PollOutcome::Halted;
  Reply reply = Reply::Denied;
  Step step{};
  if (!registered) {
    if (!request(0, "-", reply, step) || reply != Reply::Registered) return PollOutcome::BootUnavailable;
    registered = true;
    return PollOutcome::Registered;
  }
  if (!request(1, "-", reply, step)) {
    halted = true;
    return PollOutcome::Halted;
  }
  if (reply == Reply::Ready) return PollOutcome::Idle;
  if (reply == Reply::Reconcile) return PollOutcome::Waiting;
  if (reply != Reply::Step && reply != Reply::Power) {
    halted = true;
    return PollOutcome::Halted;
  }
  send(reply == Reply::Power, step.to > step.from);
  if (!request(2, step.id, reply, step) || reply != Reply::Completed) {
    halted = true;
    return PollOutcome::Halted;
  }
  return PollOutcome::Sent;
}
}
