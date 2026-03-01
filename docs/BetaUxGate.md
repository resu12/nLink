# Beta UX Gate

This checklist defines beta-blocking UX invariants for nLink. It covers only observable behavior, not visual taste.

## 1. Navigation Sanity

### No dead-ends in primary flows
- Applies to: `Home`, `Helper`, `Helpee`, `ChatView`
- Expectation: Every primary screen has a valid way to continue, retry, or go back home without restarting the app.
- How to verify manually:
  1. Open each primary flow from `Home`.
  2. Move forward into its next state, then use the visible back/retry/end action.
  3. Confirm the app remains usable and does not strand the user on a non-actionable screen.

### Diagnostics Back returns to previous page
- Applies to: `Diagnostics`, `Home`, `Helper`, `Helpee`
- Expectation: Opening Diagnostics must return to the prior non-Diagnostics page, not always `Home`.
- How to verify manually:
  1. Open `Diagnostics` from `Home`, then from `Helper`, then from `Helpee`.
  2. Press `Back` each time.
  3. Confirm return target matches the page Diagnostics was opened from.

## 2. State Consistency

### Connecting state is coherent
- Applies to: `Helper`, `Helpee`, `StatusBanner`
- Expectation: Connecting UI must not show contradictory actions or texts such as connected-only controls while not connected.
- How to verify manually:
  1. Start a session and observe the connecting state.
  2. Confirm chat/send-file actions are not active before connection completes.
  3. Confirm only connecting-relevant actions remain available.

### Connected state is coherent
- Applies to: `Helper`, `Helpee`, `ChatView`
- Expectation: Once connected, chat is usable and connection-failure UI is not shown as the primary state.
- How to verify manually:
  1. Complete a successful session.
  2. Confirm chat input is enabled and session actions work.
  3. Confirm failure/retry-only UI is not shown as the main state.

### Failed and Ended states are distinct
- Applies to: `Helper`, `Helpee`, `StatusBanner`, `ChatView`
- Expectation: User-ended sessions must not be relabeled as connection failures; failed sessions must not appear as successful completion.
- How to verify manually:
  1. End one session via the in-chat disconnect action.
  2. Trigger one genuine failure/disconnect path.
  3. Confirm the resulting text and available actions differ appropriately.

## 3. Text Policy

### No raw exceptions in main UI
- Applies to: `Home`, `Helper`, `Helpee`, `Diagnostics`, `ChatView`, `StatusBanner`
- Expectation: User-facing surfaces must not display stack traces, exception type names, or raw bridge/runtime errors as primary copy.
- How to verify manually:
  1. Exercise failure paths and startup-warning paths.
  2. Inspect visible titles, messages, and inline status text.
  3. Confirm raw exception content appears only in diagnostics/logging, not in the main UI.

### No technical IDs in primary copy
- Applies to: `Helper`, `Helpee`, `ChatView`, `StatusBanner`
- Expectation: Correlation IDs, transport IDs, and similar internal identifiers must not appear in the main user message area.
- How to verify manually:
  1. Open normal and failure states.
  2. Check visible title/message/action text on the main screen.
  3. Confirm technical identifiers are limited to diagnostic/details areas only.

## 4. Control Gating

### Disabled actions match actual capability
- Applies to: `Helper`, `Helpee`, `ChatView`
- Expectation: Buttons must be disabled or hidden when the action cannot currently succeed.
- How to verify manually:
  1. Check idle, connecting, connected, failed, and ended states.
  2. Try `Connect`, `Retry`, `Disconnect`, chat send, and file-send where applicable.
  3. Confirm unavailable actions are not falsely presented as ready.

### EndSession is idempotent
- Applies to: `ChatView`, `Helper`, `Helpee`
- Expectation: Repeated disconnect/end clicks must not trigger repeated execution or unstable UI transitions.
- How to verify manually:
  1. Enter a connected chat session.
  2. Click `Disconnect` multiple times.
  3. Confirm the first click takes effect and later clicks do nothing harmful.

## 5. Resize Sanity

### Primary actions remain visible and usable
- Applies to: `Home`, `Helper`, `Helpee`, `ChatView`, `Diagnostics`
- Expectation: Resizing the window must not clip or hide the current screen's primary action buttons.
- How to verify manually:
  1. Resize from a typical desktop size down to a smaller but supported window.
  2. Check the active screen's primary action buttons.
  3. Confirm buttons remain readable, reachable, and not clipped.

## 6. Failure UX

### Failure states show one clear next action
- Applies to: `Helper`, `Helpee`, `Diagnostics`, `StatusBanner`
- Expectation: Each failure state must make the next step obvious: `Retry`, `Start new session`, or `Diagnostics`.
- How to verify manually:
  1. Trigger rejection, disconnect, and startup/failure states.
  2. Inspect the main failure text and available actions.
  3. Confirm the user has one clear next step and is not presented with conflicting recovery guidance.
