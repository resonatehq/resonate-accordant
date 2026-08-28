# Where the three models disagree

Three things describe this protocol, and they do not agree:

- **spec** — `resonatehq/resonate-specification`, the Lean specification. Declared
  the reference by `resonate/examples/specdiff.rs`.
- **server** — the running server (`resonatehq/resonate@core-crate`) and the
  oracle it is diffed against. `diff/differential.rs` asserts every engine
  matches the oracle, so an oracle answer is an engine answer wherever the
  differential reaches; rows below marked ✔ were measured on the live server
  anyway, over raw HTTP.
- **model** — this repository. Which of the other two it follows was, until
  now, decided rule by rule without the disagreement being written down.

`resonate/examples/specdiff.rs` scores **17 agree, 17 differ of 34 probed**
between spec and oracle. This file adds the third column, and says what the
model can and cannot currently see. Nothing here is a verdict — as specdiff's
own header puts it, a difference may be deliberate.

Measured 2026-08-28 against `core-crate` @ `fa16959`.

## Rulings

Decided so far. The model already states each of these, so what a ruling
changes is which of the other two documents is wrong.

| rule | ruling | model | who is wrong |
|---|---|---|---|
| a valid address is any valid URL | server's answer | states it | **the spec** (`validation.lean:72` is scheme-aware) |
| awaited not external → 422 | spec's answer | states it | the server |
| duplicate awaited ids → 400 | spec's answer | states it | the server |
| timer + target → 400 | spec's answer | states it | the server |

The address ruling moves the three `register_listener` address rows out of
group B: they are no longer this model quietly following the server, they are
a pending change to `validation.lean`. The server's own rationale is the
argument for it — validity has to be a pure function of the string, the same
on a deployment whose poll worker is off as on one where it is on, and a
predicate that knows `poll://`'s syntax and refuses `gcps://` outright is the
scheme-aware check that reasoning rules out.

Still open:

- **the external gate on `register_listener`.** The spec answers it (422,
  `external.lean:91`, the same gate `register_callback` has) and the model
  states that answer, at a cost of 9 generated cases. Deciding the other way
  is a third change to the spec, not to the server.
- **origins.** The model says 400 across origins for `register_callback` and
  `suspend`, matching the server; the spec has no origin notion at all.
- **group C**, where the model has no position to rule on yet.

## A. The model states the spec's answer — red today

These are the failing legs. Each is the model reporting the server against the
specification.

| scenario | spec | server | model | shows up in |
|---|---|---|---|---|
| `register_callback` where the awaited is not external (`external.lean:74`) | 422 | 200 ✔ | 422 | trace, gen ×2, fuzz |
| `register_listener` where the awaited is not external (`external.lean:91`) | 422 | 200 ✔ | 422 | gen ×9 |
| `task.suspend` awaiting a non-external promise (`external.lean:253`) | 422 | 200 ✔ | 422 | trace |
| `task.suspend` naming the same awaited twice (`external.lean:275`) | 400 | 200 ✔ | 400 | trace |
| `promise.create` with both `resonate:timer` and `resonate:target` (`validation.lean:43`) | 400 | 200 ✔ | 400 | trace |

The first three are one server gap seen from three doors: the server checks
that the *awaiter* carries `resonate:target`, and has no awaitability notion
for the *awaited* side at all.

## B. The model states the server's answer — green, and silently against the spec

These pass. They are the ones to look at hardest, because a passing suite is
not evidence of agreement here.

| scenario | spec | server | model |
|---|---|---|---|
| `register_listener` on `poll://` with no `@group` | 400 | 200 ✔ | 200 |
| `register_listener` on a `gcps://` address | 400 | 200 | 200 |
| `register_listener` on a non-http, non-poll URI | 400 | 200 | 200 |
| `register_callback` across different origins | 200 | 400 ✔ | 400 |
| `task.suspend` awaiting a different origin | 200 | 400 | 400 |

The three address rows were the spec's answer until commit `3efe9f3` changed
`AddressValid` to "any URI with a scheme", matching `resonate-core`'s
`address.rs`. The specification's `addressValid` (`validation.lean:72`) is
`http:// || https:// || (poll:// && contains '@')` — the predicate the model
originally had.

The origin rows were never the spec's answer: **there is no origin notion
anywhere in `spec/`**. `promiseRegisterCallback` goes self→400, awaited
missing→404, awaiter missing→422, awaiter untargeted→422, awaited not
external→422, and stops.

## C. The model has no position — it cannot spell the scenario

| scenario | spec | server | why the model is silent |
|---|---|---|---|
| `promise.create` with an unparseable `resonate:target` | 200 | 400 ✔ | `WithTarget` always sends a valid target |
| `task.create` with both timer and target | 400 | 200 ✔ | `CreateTask` has no `Timer` field |
| `task.suspend` whose action awaiter is not the task id | 200 | 400 | the adapter always sets awaiter = task id |
| `task.heartbeat` naming tasks of different origins | 200 | 400 ✔ | the model's heartbeat is single-task, always 200 |
| `promise.search` / `task.search` / `schedule.search` | 501 | 200 ✔ | search is not modelled |
| `schedule.create` with timer+target in `promiseTags` | 400 | 200 | schedules are not modelled |

Each is a small addition to the request records if it is worth stating.

## D. Differences that are not a status code

From specdiff's notes. The model's position on the second is the spec's; the
first is unmodelled; the last two it cannot observe at all, for want of the
`debug.messages` egress tap.

- heartbeat on a task whose promise has expired — spec refuses; oracle then
  reports the task fulfilled.
- `register_callback` on an already-settled awaited, for a suspended awaiter —
  spec leaves the awaiter suspended (200, no writes); oracle makes it pending.
  The model says `SameState()`, so it takes the spec's side, but no test is
  known to drive a read that would catch the difference.
- a listener on an `external`-tagged untargeted promise — spec: the sweep
  settles it and the listener is told (1 unblock); oracle: 0 messages.
- the same for a `timer`-tagged untargeted promise.

## Not divergences at all

Two failing legs are neither the server's fault nor the spec's:

- three `trace` failures need `debug.messages`, an op the server does not
  implement — a missing test tap, not a protocol difference;
- `indefinite-selftest` fails 3 of 9 fabricating its own responses, never
  touching the server. That is a bug in this model's indefinite-failure
  branching.
