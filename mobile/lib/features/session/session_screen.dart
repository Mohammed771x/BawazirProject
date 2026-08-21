import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/audio/speech_provider.dart';
import '../../core/audio/speech_recognition_service.dart';
import '../../core/audio/speech_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../../core/widgets/speaker_button.dart';
import '../auth/session_controller.dart';
import '../hub/hub_screen.dart';
import 'session_widgets.dart';
import 'word_lookup_sheet.dart';

/// One skill session, driven entirely by the payload the backend issues.
///
/// The screen never decides whether an answer is right, whether a word passed,
/// or when it comes back — it submits and renders what it is told (rule R1).
class SessionScreen extends ConsumerStatefulWidget {
  const SessionScreen({super.key, required this.skill});

  final SkillType skill;

  @override
  ConsumerState<SessionScreen> createState() => _SessionScreenState();
}

class _SessionScreenState extends ConsumerState<SessionScreen> {
  final TextEditingController _freeText = TextEditingController();
  final TextEditingController _chatInput = TextEditingController();
  final List<_ChatMessage> _chat = [];
  final List<String> _tiles = [];

  SkillSession? _session;
  SessionResult? _result;
  ApiException? _error;

  bool _loading = true;
  bool _busy = false;
  bool _contentDone = false;
  bool _speakingFinished = false;

  /// Whether the learner has been through the words this conversation is
  /// about. A resumed conversation is already past this — repeating it would
  /// stall a conversation mid-flow.
  bool _speakingBriefed = false;

  /// The warm-up queue: the words still to be recalled correctly. A miss goes
  /// to the back rather than being dropped, so the loop ends only when every
  /// word has been answered right at least once (§2).
  final List<WarmupWord> _warmupQueue = [];

  /// The verdict on the word just answered, held long enough to show it.
  WarmupResult? _warmupResult;

  /// The item the **server** says to show. The client never advances the queue
  /// itself, because a wrong answer requeues the item and only the backend
  /// knows the retry budget (rule R1, demo review §29).
  String? _currentItemId;

  /// Where the voice conversation is right now. The learner never drives this
  /// — it advances as speech and recognition complete.
  _VoicePhase _voice = _VoicePhase.idle;

  /// True while the conversation is hands-free. Switched off when the learner
  /// chooses to type, and when the device cannot listen at all.
  bool _voiceMode = true;

  /// What the recogniser has heard so far this turn, shown as it arrives.
  String _heard = '';

  AnswerResult? _lastAnswer;
  WritingEvaluation? _lastWriting;
  SessionProgress? _progress;
  String? _selectedOption;
  /// How many rungs of the spelling hint ladder the learner has asked for.
  /// Zero means only the clue the task opened with (Part 2 §38–§40).
  int _hintStep = 0;

  /// Captured eagerly in [initState] because `ref` must not be touched during
  /// [dispose] — Riverpod throws once the element is being torn down, which
  /// would turn "learner backs out of a session" into a crash. A `late final`
  /// initialiser is not enough: it would defer the read to first use, which is
  /// exactly the disposed moment we are avoiding.
  late final WordOsApi _api;

  /// Copy for code paths with no build context to hand — reporting a failure
  /// still has to happen in the learner's language.
  AppStrings get _s => ref.read(stringsProvider);

  /// Captured for the same reason as [_api]: `ref` must not be touched once
  /// teardown has started, and the conversation loop outlives a frame.
  late final SpeechService _speech;
  late final SpeechRecognitionService _mic;

  @override
  void initState() {
    super.initState();
    _api = ref.read(wordOsApiProvider);
    _speech = ref.read(speechServiceProvider);
    _mic = ref.read(speechRecognitionProvider);
    _start();
  }

  @override
  void dispose() {
    // Backing out no longer discards the session. The server keeps it open and
    // the Skills Hub offers it again, which is the same path that recovers a
    // session after the app is killed — and it means a mis-tap on "back" does
    // not cost the learner the answers they have already given. The words are
    // not consumed either way: nothing is applied until the session completes.
    // The microphone and the voice must not outlive the screen — a learner who
    // backs out mid-turn should not be recorded, or talked at.
    _mic.cancel().ignore();
    _speech.stop().ignore();
    _freeText.dispose();
    _chatInput.dispose();
    super.dispose();
  }

  bool get _needsContentPhase =>
      widget.skill == SkillType.reading || widget.skill == SkillType.listening;

  SessionItem? get _currentItem {
    final session = _session;
    final id = _currentItemId;
    if (session == null || id == null) return null;
    return session.items.where((i) => i.id == id).firstOrNull;
  }

  bool get _answered => _lastAnswer != null || _lastWriting != null;

  /// The target word's surface form, so the context passage can highlight it.
  String? _targetTextFor(SessionItem item) {
    final session = _session;
    if (session == null || item.wordId == null) return null;
    return session.targetWords
        .where((w) => w.wordId == item.wordId)
        .firstOrNull
        ?.text;
  }

  /// What this item asks, in the learner's own language.
  ///
  /// A fixed instruction arrives as a key and is said here; anything written
  /// for this session — a comprehension question and its options — arrives as
  /// text and is shown exactly as it is, because that text is the English the
  /// learner is here to read (ADR-035).
  String _instruction(AppStrings s, SessionItem item) => item.promptKey == null
      ? item.prompt
      : s.sessionPrompt(item.promptKey, _targetTextFor(item) ?? '');

  /// True once the learner has chosen to practise instead of waiting (§5).
  bool _practice = false;

  Future<void> _start() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      // Resuming is the same call: the server returns the open session for this
      // skill if there is one, so a killed app picks up exactly where it was
      // (and does not spend a second AI call on a new passage).
      final session = await _api.startSession(widget.skill, practice: _practice);
      if (!mounted) return;

      // Where to continue is the server's answer, not `items.first` — on a
      // resumed session the early items are already cleared, and a requeued one
      // may be waiting at the back of the queue.
      final progress = session.progress ??
          SessionProgress(
            nextItemId: session.items.isEmpty ? null : session.items.first.id,
            remaining: session.items.length,
            answered: 0,
            total: session.items.length,
          );

      setState(() {
        _session = session;
        _loading = false;
        // A resumed session skips straight back to the questions: the learner
        // has already read the passage. Keyed on `attempted`, not `answered` —
        // a first answer that was wrong clears nothing, and keying off
        // `answered` would send that learner back to re-read.
        _contentDone = !_needsContentPhase || progress.attempted;
        _currentItemId = progress.nextItemId;
        _progress = progress;
        if (session.conversation != null) {
          // A resumed conversation comes back with its history; a fresh one
          // carries only the opening line.
          final turns = session.conversation!.turns;
          _chat.clear();
          if (turns.isEmpty) {
            _chat.add(_ChatMessage(session.conversation!.opening, fromAi: true));
          } else {
            _chat.addAll(turns
                .map((t) => _ChatMessage(t.text, fromAi: t.fromAi)));
          }
          // The learner has already spoken, so this is a conversation being
          // resumed rather than one about to start.
          _speakingBriefed = turns.any((t) => !t.fromAi);
        }

        // §1 and §7: a warm-up only when there is something to warm up on.
        // No words due means straight into the conversation.
        if (widget.skill == SkillType.speaking && !_speakingBriefed) {
          _warmupQueue
            ..clear()
            ..addAll(session.warmup);
          if (_warmupQueue.isEmpty) _speakingBriefed = true;
        }
      });
      // The conversation starts speaking on its own — but only once the
      // learner has seen which words it is about (§26). A resumed conversation
      // has been briefed already and simply carries on.
      if (widget.skill == SkillType.speaking && _chat.isNotEmpty) {
        if (_speakingBriefed) _resumeSpeaking();
      }

      // Every question answered, and the session never closed — the app was
      // killed between the last answer and the result screen, or a connection
      // dropped there. The server rightly keeps such a session open, so it is
      // handed back on the next visit with nothing left to ask: no current
      // item, and a screen that waits for one for ever.
      //
      // There is nothing to ask, so there is nothing to wait for. Finishing it
      // is what the learner was one tap away from doing.
      if (_needsFinishing) {
        await _complete();
        return;
      }
    } catch (rawError) {
      // Everything, not only a refusal from the server. A malformed response or
      // a bug on this screen must still end the wait: a spinner with no end
      // says nothing and there is no way out of it but to kill the app.
      //
      // `ApiException.from` rather than `rawError.toString()` — a Dart error
      // message is English, internal, and sometimes quotes the very data that
      // broke it, none of which belongs on a learner's screen.
      if (!mounted) return;
      setState(() {
        _error = ApiException.from(rawError);
        _loading = false;
      });
    }
  }

  /// True when the session has been answered through but never closed.
  bool get _needsFinishing {
    final session = _session;
    final progress = _progress;
    if (session == null || progress == null || _result != null) return false;

    // A conversation has no items to count; it ends by its own rules.
    if (widget.skill == SkillType.speaking) return false;

    return progress.nextItemId == null && progress.remaining == 0;
  }

  /// Picks the conversation back up from whatever the tutor last said.
  void _resumeSpeaking() {
    final lastFromAi = _chat.isNotEmpty && _chat.last.fromAi
        ? _chat.last.text
        : null;
    if (lastFromAi != null) unawaited(_speakThenListen(lastFromAi));
  }

  Future<void> _answer(SessionItem item, String answer) async {
    if (_answered || _busy) return;
    setState(() => _busy = true);
    try {
      final result = await _api.submitAnswer(
            sessionId: _session!.id,
            itemId: item.id,
            answer: answer,
          );
      if (mounted) {
        setState(() {
          _lastAnswer = result;
          _progress = result.progress;
        });
      }
    } catch (rawError) {
      final e = ApiException.from(rawError);
      _handleSubmitFailure(e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// A failed submission must never strand the learner mid-session. If the
  /// server says the item is stale the client resynchronises instead of
  /// blocking; anything else is reported and the answer can be retried.
  void _handleSubmitFailure(ApiException e) {
    if (e.code == 'ITEM_NOT_CURRENT') {
      _snack(_s.apiError(e.code, e.message));
      setState(() {
        _selectedOption = null;
        _currentItemId = _progress?.nextItemId ?? _currentItemId;
      });
      return;
    }
    if (e.code == 'SESSION_NOT_FOUND') {
      setState(() => _error = e);
      return;
    }
    _snack(_s.apiError(e.code, e.message));
  }

  Future<void> _submitWriting(SessionItem item) async {
    if (_busy) return;
    final sentence = _freeText.text.trim();
    if (sentence.isEmpty) return;
    setState(() => _busy = true);
    try {
      final evaluation = await _api.submitWriting(
            sessionId: _session!.id,
            itemId: item.id,
            sentence: sentence,
          );
      if (mounted) {
        setState(() {
          _lastWriting = evaluation;
          _progress = evaluation.progress;
        });
      }
    } catch (rawError) {
      final e = ApiException.from(rawError);
      _handleSubmitFailure(e);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  // ── The voice conversation loop ──────────────────────────────────────────
  //
  //   tutor speaks (TTS) → microphone opens → learner talks → silence ends the
  //   turn → text goes to the backend → reply comes back → tutor speaks it →
  //   microphone opens again
  //
  // The learner presses nothing. Each step waits for the previous one to
  // *finish* rather than guessing at a delay: an open microphone during
  // playback records the tutor's own voice, and a delay long enough to be safe
  // is long enough to feel broken.

  /// Speaks a tutor turn, then hands the microphone to the learner.
  /// Speaks the tutor's line, then hands the turn to the learner.
  ///
  /// The microphone does **not** open by itself. The tutor talks, the mic stays
  /// shut, and the learner taps when they are ready — otherwise the recogniser
  /// is running while the learner is still thinking, and the first thing it
  /// hears is silence or the tutor's own voice.
  Future<void> _speakThenListen(String text) async {
    if (!mounted) return;
    setState(() => _voice = _VoicePhase.speaking);

    await _speech.speakToCompletion('tutor:${text.hashCode}', text);
    if (!mounted || _speakingFinished) return;

    // A device that cannot listen falls back to typing rather than showing a
    // microphone that will never work.
    final canListen = await _mic.initialise();
    if (!mounted) return;

    setState(() => _voice =
        canListen ? _VoicePhase.idle : _VoicePhase.unavailable);
  }

  /// Opens the microphone and leaves it open until the learner says they are
  /// done.
  ///
  /// Push-to-talk. Ending a turn on silence cut learners off mid-sentence:
  /// someone searching for a word in a foreign language pauses constantly, and
  /// no amount of tuning a timer fixes that — only letting them decide does.
  Future<void> _startListening() async {
    if (!mounted || _busy || _speakingFinished) return;

    setState(() {
      _voice = _VoicePhase.listening;
      _heard = '';
    });

    final started = await _mic.startListening(
      // Their words appear as they speak, so a long pause never looks like a
      // dead microphone.
      onPartial: (heard) {
        if (mounted) setState(() => _heard = heard);
      },
    );

    if (!mounted) return;
    if (!started) setState(() => _voice = _VoicePhase.unavailable);
  }

  /// Throws away what was said and offers the microphone again (ADR-059).
  ///
  /// The second tap on the microphone sends, and until now that was the only
  /// way out of a recording: a learner who fumbled a sentence had to send the
  /// fumble and let the tutor answer it. This is the other exit — the words are
  /// dropped, nothing is sent, no AI call is spent, and the turn is theirs to
  /// take again.
  Future<void> _discardListening() async {
    if (!mounted || _voice != _VoicePhase.listening) return;

    // `cancel`, not `stopAndRead`: stopping asks the recogniser for its result,
    // and the point here is that there is not going to be one.
    await _mic.cancel();
    if (!mounted) return;

    setState(() {
      _voice = _VoicePhase.idle;
      _heard = '';
    });
  }

  /// Closes the microphone and sends whatever was said.
  Future<void> _stopAndSend() async {
    if (!mounted || _voice != _VoicePhase.listening) return;

    setState(() => _voice = _VoicePhase.thinking);
    final said = await _mic.stopAndRead();
    if (!mounted) return;

    if (said == null || said.trim().isEmpty) {
      // Nothing usable. The turn is offered again rather than sent — an empty
      // transcript would spend an AI call and confuse the tutor.
      setState(() {
        _voice = _VoicePhase.idle;
        _heard = '';
      });
      return;
    }

    await _sendChat(said);
  }

  /// Sends one learner turn, from voice or from the keyboard.
  Future<void> _sendChat([String? spoken]) async {
    final text = (spoken ?? _chatInput.text).trim();
    if (text.isEmpty || _busy) return;

    setState(() {
      _chat.add(_ChatMessage(text, fromAi: false));
      _chatInput.clear();
      _heard = '';
      _busy = true;
      _voice = _VoicePhase.thinking;
    });

    try {
      final turn = await _api.submitSpeakingTurn(
            sessionId: _session!.id,
            transcript: text,
          );
      if (!mounted) return;
      setState(() {
        _chat.add(_ChatMessage(turn.aiMessage, fromAi: true));
        _speakingFinished = turn.isFinal;
        _busy = false;
      });

      if (turn.isFinal) {
        // The server ended the conversation. The last reply is still spoken —
        // being cut off mid-goodbye is worse than waiting a moment for the
        // result — and only then is the session completed and evaluated.
        setState(() => _voice = _VoicePhase.speaking);
        await _speech.speakToCompletion('tutor:${turn.aiMessage.hashCode}', turn.aiMessage);
        if (mounted) await _complete();
        return;
      }

      // Straight back around the loop.
      if (_voiceMode) {
        await _speakThenListen(turn.aiMessage);
      } else {
        setState(() => _voice = _VoicePhase.idle);
      }
    } catch (rawError) {
      final e = ApiException.from(rawError);
      _snack(_s.apiError(e.code, e.message));
      if (mounted) setState(() => _voice = _VoicePhase.idle);
    } finally {
      if (mounted && _busy) setState(() => _busy = false);
    }
  }

  void _next() {
    // Leaving this question stops whatever it was playing. A sentence that
    // carries on talking over the next question is the audio equivalent of a
    // page that did not turn.
    _speech.stop();

    final nextId = _progress?.nextItemId;
    if (nextId == null) {
      _complete();
      return;
    }
    setState(() {
      _currentItemId = nextId;
      _lastAnswer = null;
      _lastWriting = null;
      _selectedOption = null;
      _hintStep = 0;
      _freeText.clear();
      _tiles.clear();
      _usedTileIndexes.clear();
    });
  }

  Future<void> _complete() async {
    // The session is over; nothing from it should still be speaking while the
    // learner reads their result.
    _speech.stop();
    setState(() => _busy = true);
    try {
      final result =
          await _api.completeSession(_session!.id);
      if (mounted) setState(() => _result = result);
    } catch (rawError) {
      final e = ApiException.from(rawError);
      _snack(_s.apiError(e.code, e.message));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _snack(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
        .showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    final color = SkillVisuals.color(context, widget.skill);

    return Scaffold(
      appBar: AppBar(
        // A practice session says so in its title (§5): the learner must never
        // finish a session unsure whether it counted.
        title: Text(
          _session?.isPractice == true
              ? '${s.skillName(widget.skill)} · ${s.practiceSession}'
              : s.skillName(widget.skill),
          // Larger than the theme's default title: this and the level beside
          // it are the two things a learner reads to know where they are.
          style: context.text.headlineSmall,
        ),
        actions: [
          if (_session != null && _result == null)
            Padding(
              padding: const EdgeInsetsDirectional.only(end: AppSpacing.md),
              child: Center(child: _levelControl(s, color)),
            ),
        ],
      ),
      body: SafeArea(child: _body(s, color)),
    );
  }

  /// The CEFR badge — and, while the passage is still unanswered, the way to
  /// change it.
  ///
  /// A learner who finds the text too hard should not have to abandon the
  /// session. Tapping re-tells *this* passage at another level (§4); once the
  /// questions begin it is a plain badge again, because re-telling would throw
  /// away the answers they have already given.
  Widget _levelControl(AppStrings s, Color color) {
    final session = _session!;
    // A conversation has no passage to replace: the level is an input to the
    // next thing the tutor says, so it can change at any point and takes
    // effect immediately (§4). A passage is in front of the learner, so its
    // level locks once the questions begin.
    // Speaking and Writing have no passage to replace: the level is an input
    // to what happens next — the tutor's reply, or the rewrite the learner is
    // shown — so it can change at any point and takes effect immediately.
    // Reading and Listening put a text in front of the learner, so their level
    // locks once the questions begin. Spelling has no level of its own
    // (ADR-008).
    final canChange = switch (widget.skill) {
      SkillType.speaking => !_speakingFinished,
      SkillType.writing => _result == null,
      SkillType.reading || SkillType.listening => !_contentDone &&
          (session.content?.canChangeLevel ?? false),
      _ => false,
    };

    final badge = LevelBadge(
      label: session.levelUsed.label,
      color: color,
      // Bigger, and marked as something you can press.
      size: 15,
      trailing: canChange ? Icons.expand_more_rounded : null,
    );

    if (!canChange) return badge;

    return InkWell(
      borderRadius: BorderRadius.circular(999),
      onTap: _busy ? null : () => _pickLevel(s),
      child: badge,
    );
  }

  Future<void> _pickLevel(AppStrings s) async {
    final current = _session!.levelUsed;

    final chosen = await showModalBottomSheet<CefrLevel>(
      context: context,
      showDragHandle: true,
      builder: (sheetContext) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(
                  AppSpacing.md, 0, AppSpacing.md, AppSpacing.xs),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(s.changeLevelTitle,
                      style: sheetContext.text.titleMedium),
                  const SizedBox(height: AppSpacing.xxs),
                  Text(
                    widget.skill == SkillType.speaking
                        ? s.changeLevelHintSpeaking
                        : s.changeLevelHint,
                    style: sheetContext.text.bodySmall?.copyWith(
                      color: sheetContext.colors.onSurface
                          .withValues(alpha: 0.7),
                    ),
                  ),
                ],
              ),
            ),
            // Chips rather than a list: there are a dozen bands, and as rows
            // they overflow the sheet on a phone. As chips the whole ladder is
            // visible at once, which is also how a learner thinks about it.
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: AppSpacing.md),
              child: Wrap(
                spacing: AppSpacing.xs,
                runSpacing: AppSpacing.xs,
                children: [
                  for (final level in CefrLevel.values)
                    ChoiceChip(
                      label: Text(level.label),
                      selected: level == current,
                      onSelected: (_) =>
                          Navigator.of(sheetContext).pop(level),
                    ),
                ],
              ),
            ),
            const SizedBox(height: AppSpacing.lg),
          ],
        ),
      ),
    );

    if (chosen == null || chosen == current || !mounted) return;
    await _changeLevel(chosen);
  }

  Future<void> _changeLevel(CefrLevel level) async {
    setState(() => _busy = true);
    try {
      // The passage being replaced must stop being read aloud.
      await _speech.stop();

      final session = await _api.changeSessionLevel(_session!.id, level);
      if (!mounted) return;

      setState(() {
        _session = session;
        _progress = session.progress;
        _lastAnswer = null;
        _selectedOption = null;

        // A conversation and a writing task keep their place: only the level
        // changed. A passage was replaced, so its questions start again from
        // the first.
        if (widget.skill != SkillType.speaking &&
            widget.skill != SkillType.writing) {
          _currentItemId =
              session.items.isEmpty ? null : session.items.first.id;
        }
      });

      // The level the learner just chose is now their level for this skill, on
      // the server. Settings and the hub both render it from the cached
      // profile, so without this they keep showing the old band until the next
      // sign-in — the learner changes it here and finds it unchanged there.
      await ref.read(sessionProvider.notifier).refresh();
      ref.invalidate(hubProvider);
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (mounted) _snack(_s.apiError(e.code, e.message));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Widget _body(AppStrings s, Color color) {
    if (_loading) return BusyView(message: s.loading);

    if (_error != null) {
      if (_error!.code == 'NO_WORDS_DUE') {
        // Reading and Listening still work without vocabulary, so a learner who
        // came to practise is offered a practice session rather than a closed
        // door (§5). The other three need words to be about anything, and
        // pretending otherwise would waste the learner's time.
        final canPractise = widget.skill == SkillType.reading ||
            widget.skill == SkillType.listening;

        return EmptyState(
          icon: Icons.hourglass_bottom_rounded,
          title: s.noWordsDue,
          message: canPractise ? s.practiceOfferBody : s.noWordsDueBody,
          action: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (canPractise)
                FilledButton.icon(
                  onPressed: () {
                    setState(() => _practice = true);
                    unawaited(_start());
                  },
                  icon: const Icon(Icons.auto_stories_outlined),
                  label: Text(s.practiseAnyway),
                ),
              const SizedBox(height: AppSpacing.xs),
              OutlinedButton(
                onPressed: () => Navigator.of(context).maybePop(),
                child: Text(s.backToHub),
              ),
            ],
          ),
        );
      }
      // Through `apiError`, like every other failure the learner reads: this
      // one used to print the server's English sentence straight onto the
      // screen, which in an Arabic app reads as a crash (ADR-035).
      //
      // Retry is offered only where trying again could actually work. A
      // finished session or a question that has moved on will answer exactly
      // the same way a second time, and a button that cannot help is worse
      // than no button — it makes the learner press it repeatedly.
      return ErrorView(
        message: s.apiError(_error!.code, _error!.message),
        retryLabel: s.retry,
        onRetry: _error!.isRetryable ? _start : null,
      );
    }

    if (_result != null) {
      return SessionResultView(
        result: _result!,
        transcript: widget.skill == SkillType.listening
            ? _session?.content
            : null,
        onClose: () => Navigator.of(context).maybePop(),
      );
    }

    if (_busy && _session != null && _chat.isEmpty && !_contentDone) {
      return BusyView(message: s.evaluating);
    }

    if (widget.skill == SkillType.speaking) return _speakingView(s, color);

    final body = !_contentDone ? _contentView(s, color) : _questionView(s, color);
    if (_session?.isPractice != true) return body;

    return Column(
      children: [
        Container(
          width: double.infinity,
          color: context.palette.subtleSurface,
          padding: const EdgeInsets.symmetric(
            horizontal: AppSpacing.md,
            vertical: AppSpacing.xs,
          ),
          child: Text(
            s.practiceNotCounted,
            style: context.text.labelMedium?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.7),
            ),
          ),
        ),
        Expanded(child: body),
      ],
    );
  }

  // ── Reading / Listening content ───────────────────────────────────────────
  Widget _contentView(AppStrings s, Color color) {
    final session = _session!;
    final content = session.content;
    if (content == null) {
      return BusyView(message: s.loading);
    }
    final isListening = widget.skill == SkillType.listening;

    return Column(
      children: [
        Expanded(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(AppSpacing.md),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  isListening ? s.listenCarefully : s.readPassage,
                  style: context.text.titleMedium,
                ),
                const SizedBox(height: AppSpacing.md),
                if (isListening)
                  _ListeningPlayer(text: content.text, color: color)
                else
                  AppCard(
                    child: HighlightedPassage(
                      content: content,
                      color: color,
                      onWordTap: (word, {required isTarget}) => showWordLookup(
                        context,
                        word: word,
                        isTarget: isTarget,
                        color: color,
                        // The meaning the generator gave this word in this
                        // sentence. Null falls back to the dictionary, which
                        // can only offer every sense the word has ever had.
                        inContext: content.glossaryFor(word),
                      ),
                    ),
                  ),
                if (!isListening) ...[
                  const SizedBox(height: AppSpacing.sm),
                  Text(
                    s.tapAnyWord,
                    style: context.text.labelMedium?.copyWith(
                      color: context.colors.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                ],
                const SizedBox(height: AppSpacing.md),
                if (!isListening)
                  Wrap(
                    spacing: AppSpacing.xs,
                    runSpacing: AppSpacing.xs,
                    children: [
                      for (final word in session.targetWords)
                        StatusPill(
                          label: word.text,
                          color: color,
                          icon: Icons.bookmark_border_rounded,
                        ),
                    ],
                  ),
              ],
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(
            onPressed: () {
              // The passage is finished with. Its audio must not follow the
              // learner into the questions — where, for Listening, it would
              // also be handing them the answers.
              _speech.stop();
              setState(() => _contentDone = true);
            },
            child: Text(
              isListening ? s.iFinishedListening : s.iFinishedReading,
            ),
          ),
        ),
      ],
    );
  }

  // ── Questions / tasks ─────────────────────────────────────────────────────
  Widget _questionView(AppStrings s, Color color) {
    final session = _session!;
    final item = _currentItem;

    // Reached only if the queue and the pointer disagree. Finishing is the
    // honest response: there is no question to show, and a spinner here waits
    // for something that is never coming.
    if (item == null) {
      return Center(
        child: Padding(
          padding: AppSpacing.page,
          child: FilledButton(
            onPressed: _busy ? null : _complete,
            child: Text(s.finish),
          ),
        ),
      );
    }

    final progress = _progress;

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Text(
                      s.questionOf(
                        (progress?.answered ?? 0) + 1,
                        progress?.total ?? session.items.length,
                      ),
                      style: context.text.labelMedium?.copyWith(
                        color: context.colors.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ),
                  // A retry is labelled, so a repeated question reads as
                  // deliberate reinforcement rather than a glitch.
                  if ((_lastAnswer?.attemptNumber ?? 1) > 1 ||
                      (_lastWriting?.attemptNumber ?? 1) > 1)
                    StatusPill(
                      label: s.retryAttempt(
                        _lastAnswer?.attemptNumber ??
                            _lastWriting?.attemptNumber ??
                            1,
                      ),
                      color: context.palette.warning,
                    ),
                ],
              ),
              const SizedBox(height: AppSpacing.xs),
              StepProgressBar(value: progress?.ratio ?? 0),
            ],
          ),
        ),
        Expanded(
          child: SingleChildScrollView(
            padding: AppSpacing.page,
            child: switch (item.type) {
              SessionItemType.writingTask => _writingTask(s, item, color),
              SessionItemType.spellingTask => _spellingTask(s, item, color),
              _ => _multipleChoice(s, item, color),
            },
          ),
        ),
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: FilledButton(
            onPressed: _answered && !_busy ? _next : null,
            child: Text(
              _progress?.nextItemId == null ? s.finish : s.next,
            ),
          ),
        ),
      ],
    );
  }

  Widget _multipleChoice(AppStrings s, SessionItem item, Color color) {
    final result = _lastAnswer;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Reading: the word inside its neighbouring sentences, so the meaning
        // can be inferred from context (demo review §26–27).
        if (item.context != null) ...[
          ContextPassage(
            context: item.context!,
            highlight: _targetTextFor(item),
            color: color,
          ),
          const SizedBox(height: AppSpacing.md),
        ],
        // Listening: the same three sentences, spoken and never shown.
        if (item.audioText != null) ...[
          SentencePlayer(text: item.audioText!, color: color),
          const SizedBox(height: AppSpacing.md),
        ],
        // The question carries the same reading comfort as the passage
        // (§25) — it is read carefully, often twice, and a listening question
        // is all the learner has left once the audio has stopped.
        Text(
          item.prompt,
          style: context.text.titleMedium?.copyWith(fontSize: 18, height: 1.45),
        ),
        const SizedBox(height: AppSpacing.md),
        for (final option in item.options)
          OptionTile(
            label: option,
            enabled: result == null && !_busy,
            correct: result == null
                ? null
                : option == result.correctAnswer
                    ? true
                    : (_selectedOption == option ? false : null),
            onTap: () {
              _selectedOption = option;
              _answer(item, option);
            },
          ),
        if (result != null) ...[
          const SizedBox(height: AppSpacing.sm),
          _FeedbackBanner(
            correct: result.isCorrect,
            title: result.isCorrect ? s.correct : s.incorrect,
            body: [
              if (!result.isCorrect)
                '${s.correctAnswerIs}: ${result.correctAnswer}',
              // The explanation is shown after a right answer too — the point
              // is to leave the item understanding the word, not just scored
              // (demo review §28).
              ?result.explanation,
              if (result.requeued) s.comesBackLater,
            ].join('\n'),
          ),
        ],
        const SizedBox(height: AppSpacing.xl),
      ],
    );
  }

  Widget _writingTask(AppStrings s, SessionItem item, Color color) {
    final evaluation = _lastWriting;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(_instruction(s, item), style: context.text.titleMedium),
        const SizedBox(height: AppSpacing.md),
        TextField(
          controller: _freeText,
          maxLines: 4,
          enabled: evaluation == null,
          decoration: InputDecoration(hintText: s.writeSentence),
        ),
        const SizedBox(height: AppSpacing.sm),
        if (evaluation == null)
          FilledButton.tonal(
            onPressed: _busy ? null : () => _submitWriting(item),
            child: _busy
                ? Text(s.evaluating)
                : Text(s.checkAnswer),
          )
        else ...[
          _FeedbackBanner(
            correct: evaluation.passed,
            title: evaluation.passed ? s.correct : s.incorrect,
            body: [
              evaluation.feedback,
              if (evaluation.requeued) s.comesBackLater,
            ].join('\n'),
          ),
          if (evaluation.suggestion != null) ...[
            const SizedBox(height: AppSpacing.xs),
            AppCard(
              color: context.palette.subtleSurface,
              child: Row(
                children: [
                  Icon(Icons.lightbulb_outline_rounded,
                      size: 18, color: context.palette.warning),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          s.atYourLevel(_session!.levelUsed),
                          style: context.text.labelSmall
                              ?.copyWith(color: context.palette.warning),
                        ),
                        const SizedBox(height: AppSpacing.xxs),
                        EnglishText(
                          evaluation.suggestion!,
                          style: context.text.bodySmall,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
        const SizedBox(height: AppSpacing.xl),
      ],
    );
  }

  Widget _spellingTask(AppStrings s, SessionItem item, Color color) {
    final result = _lastAnswer;
    final tiles = item.inputMode == SpellingInputMode.letterTiles;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        AppCard(
          color: color.withValues(alpha: 0.07),
          borderColor: color.withValues(alpha: 0.28),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                s.spellingClueLabel(item.clueKind),
                style: context.text.labelSmall?.copyWith(color: color),
              ),
              const SizedBox(height: AppSpacing.xxs),
              Text(
                item.clue ?? '',
                textDirection:
                    item.clueKind == SpellingClueKind.arabicMeaning
                        ? TextDirection.rtl
                        : TextDirection.ltr,
                style: context.text.titleMedium,
              ),
            ],
          ),
        ),
        // The ladder below the clue: each press reveals the next rung, which
        // is always easier than the one before, down to the letter count.
        // Nothing is revealed unasked, and the learner never has to climb.
        if (result == null) ...[
          for (final hint in item.hints.take(_hintStep + 1).skip(1)) ...[
            const SizedBox(height: AppSpacing.xs),
            AppCard(
              color: context.palette.subtleSurface,
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.lightbulb_outline_rounded,
                      size: 18, color: context.palette.warning),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          s.spellingClueLabel(hint.kind),
                          style: context.text.labelSmall
                              ?.copyWith(color: context.palette.warning),
                        ),
                        const SizedBox(height: AppSpacing.xxs),
                        Text(
                          hint.text,
                          textDirection:
                              hint.kind == SpellingClueKind.arabicMeaning
                                  ? TextDirection.rtl
                                  : TextDirection.ltr,
                          style: context.text.titleSmall,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ],
          if (_hintStep < item.hints.length - 1) ...[
            const SizedBox(height: AppSpacing.xs),
            TextButton.icon(
              onPressed: () => setState(() => _hintStep++),
              icon: const Icon(Icons.lightbulb_outline_rounded, size: 18),
              label: Text(_hintStep == 0 ? s.showHint : s.easierHint),
            ),
          ],
        ],
        const SizedBox(height: AppSpacing.lg),
        if (tiles) ...[
          Container(
            width: double.infinity,
            padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.md,
              vertical: AppSpacing.sm,
            ),
            decoration: BoxDecoration(
              color: context.palette.subtleSurface,
              borderRadius: AppRadii.fieldBorder,
              border: Border.all(color: context.palette.border),
            ),
            child: Text(
              _tiles.join(),
              style: context.text.headlineSmall?.copyWith(letterSpacing: 2),
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          Text(s.tapLetters, style: context.text.bodySmall),
          const SizedBox(height: AppSpacing.xs),
          Wrap(
            spacing: AppSpacing.xs,
            runSpacing: AppSpacing.xs,
            children: [
              for (var i = 0; i < item.letters.length; i++)
                _LetterTile(
                  letter: item.letters[i],
                  used: _usedTileIndexes.contains(i),
                  onTap: result != null
                      ? null
                      : () => setState(() {
                            _usedTileIndexes.add(i);
                            _tiles.add(item.letters[i]);
                          }),
                ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          Row(
            children: [
              // Undo one letter. With decoy tiles in the pool (§36) a mis-tap
              // is ordinary, and making the learner retype the whole word for
              // one wrong letter punishes the wrong mistake.
              IconButton(
                onPressed: result != null || _tiles.isEmpty
                    ? null
                    : () => setState(() {
                          _tiles.removeLast();
                          _usedTileIndexes.removeLast();
                        }),
                icon: const Icon(Icons.backspace_outlined, size: 20),
                tooltip: s.undoLetter,
              ),
              TextButton(
                onPressed: result != null || _tiles.isEmpty
                    ? null
                    : () => setState(() {
                          _tiles.clear();
                          _usedTileIndexes.clear();
                        }),
                child: Text(s.clear),
              ),
              const SizedBox(width: AppSpacing.sm),
              // Expanded, not trailing after a Spacer: the theme gives every
              // FilledButton `Size.fromHeight(54)` — an infinite minimum width —
              // so an unflexed one in a Row fails layout outright.
              Expanded(
                child: FilledButton.tonal(
                  onPressed: result != null || _tiles.isEmpty || _busy
                      ? null
                      : () => _answer(item, _tiles.join()),
                  child: Text(s.checkAnswer),
                ),
              ),
            ],
          ),
        ] else ...[
          TextField(
            controller: _freeText,
            enabled: result == null,
            textCapitalization: TextCapitalization.none,
            decoration: const InputDecoration(hintText: 'Type the word'),
            style: context.text.titleMedium,
          ),
          const SizedBox(height: AppSpacing.sm),
          FilledButton.tonal(
            onPressed: result != null || _busy
                ? null
                : () => _answer(item, _freeText.text.trim()),
            child: Text(s.checkAnswer),
          ),
        ],
        if (result != null) ...[
          const SizedBox(height: AppSpacing.md),
          _FeedbackBanner(
            correct: result.isCorrect,
            title: result.isCorrect ? s.correct : s.incorrect,
            body: [
              if (!result.isCorrect)
                '${s.correctAnswerIs}: ${result.correctAnswer}',
              // The explanation is shown after a right answer too — the point
              // is to leave the item understanding the word, not just scored
              // (demo review §28).
              ?result.explanation,
              if (result.requeued) s.comesBackLater,
            ].join('\n'),
          ),
        ],
        const SizedBox(height: AppSpacing.xl),
      ],
    );
  }

  /// Which tiles have been spent, in the order they were tapped — a list, not
  /// a set, because undo has to give back the *last* one.
  final List<int> _usedTileIndexes = [];

  // ── Speaking ──────────────────────────────────────────────────────────────
  /// The words this conversation is about — recalled, not merely shown.
  ///
  /// A spoken conversation gives the learner no time to look anything up: by
  /// the time they realise they cannot remember what "allocate" means, the
  /// tutor has already asked the question. Showing the list was the first
  /// version of this; asking for the meaning is the honest one, because only
  /// one of the two tells you whether they actually know it.
  ///
  /// A miss goes to the back of the queue rather than out of it, and the loop
  /// ends when every word has been recalled correctly once.
  ///
  /// None of it is recorded. It is a warm-up, not a test: no word passes or
  /// fails here, and no level moves (§3).
  Widget _speakingWarmup(AppStrings s, Color color) {
    final word = _warmupQueue.first;
    final result = _warmupResult;

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(AppSpacing.md),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(s.beforeYouSpeak, style: context.text.titleMedium),
              const SizedBox(height: AppSpacing.xxs),
              Text(
                s.warmupHint,
                style: context.text.bodyMedium?.copyWith(
                  color: context.colors.onSurface.withValues(alpha: 0.7),
                ),
              ),
              const SizedBox(height: AppSpacing.xs),
              StatusPill(
                label: s.warmupRemaining(_warmupQueue.length),
                color: color,
              ),
            ],
          ),
        ),
        Expanded(
          child: SingleChildScrollView(
            padding: AppSpacing.page,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                AppCard(
                  color: color.withValues(alpha: 0.07),
                  borderColor: color.withValues(alpha: 0.28),
                  child: Row(
                    children: [
                      Expanded(
                        child: EnglishText(
                          word.text,
                          style: context.text.headlineSmall?.copyWith(
                            color: color,
                          ),
                        ),
                      ),
                      SpeakerButton(
                        id: 'warmup:${word.wordId}',
                        text: word.text,
                        color: color,
                        size: 24,
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: AppSpacing.md),
                for (final option in word.options)
                  OptionTile(
                    label: option,
                    enabled: result == null && !_busy,
                    correct: result == null
                        ? null
                        : option == result.correctAnswer
                            ? true
                            : (_selectedOption == option ? false : null),
                    onTap: () {
                      _selectedOption = option;
                      unawaited(_answerWarmup(word, option));
                    },
                  ),
                if (result != null && !result.isCorrect) ...[
                  const SizedBox(height: AppSpacing.sm),
                  // Told the answer, then met again at the end of the queue —
                  // which is the point: they should not walk into the
                  // conversation still unsure.
                  _FeedbackBanner(
                    correct: false,
                    title: s.incorrect,
                    body: s.comesBackLater,
                  ),
                ],
                const SizedBox(height: AppSpacing.xl),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Future<void> _answerWarmup(WarmupWord word, String answer) async {
    if (_warmupResult != null || _busy) return;
    setState(() => _busy = true);

    try {
      final result = await _api.answerWarmup(
        sessionId: _session!.id,
        wordId: word.wordId,
        answer: answer,
      );
      if (!mounted) return;

      setState(() {
        _warmupResult = result;
        _busy = false;
      });

      // A right answer moves on briskly; a wrong one holds long enough to read
      // the meaning it just showed.
      await Future<void>.delayed(Duration(
          milliseconds: result.isCorrect ? 550 : 1800));
      if (!mounted) return;

      setState(() {
        _warmupQueue.removeAt(0);
        // Missed words go to the back, never out (§2).
        if (!result.isCorrect) _warmupQueue.add(word);
        _warmupResult = null;
        _selectedOption = null;

        if (_warmupQueue.isEmpty) _speakingBriefed = true;
      });

      // Every word recalled — the conversation can start.
      if (_warmupQueue.isEmpty) _resumeSpeaking();
    } catch (rawError) {
      final e = ApiException.from(rawError);
      if (mounted) {
        setState(() => _busy = false);
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(_s.apiError(e.code, e.message))));
      }
    }
  }

  Widget _speakingView(AppStrings s, Color color) {
    final session = _session!;
    if (!_speakingBriefed && _warmupQueue.isNotEmpty) {
      return _speakingWarmup(s, color);
    }
    return Column(
      children: [
        Padding(
          padding: AppSpacing.page,
          child: AppCard(
            color: color.withValues(alpha: 0.07),
            borderColor: color.withValues(alpha: 0.28),
            child: Row(
              children: [
                Icon(Icons.flag_outlined, color: color, size: 18),
                const SizedBox(width: AppSpacing.xs),
                Expanded(
                  child: Text(
                    session.targetWords.map((w) => w.text).join(' · '),
                    style: context.text.titleSmall?.copyWith(color: color),
                  ),
                ),
              ],
            ),
          ),
        ),
        Expanded(
          child: ListView.builder(
            padding: const EdgeInsets.all(AppSpacing.md),
            itemCount: _chat.length,
            itemBuilder: (context, index) =>
                _ChatBubble(message: _chat[index], color: color),
          ),
        ),
        if (!_speakingFinished)
          Padding(
            padding: const EdgeInsets.all(AppSpacing.md),
            child: _voiceControls(s, color),
          ),
      ],
    );
  }

  /// The voice panel.
  ///
  /// It is a *status display* first and a control second: in a working
  /// conversation the learner touches none of this. The buttons exist for the
  /// cases where the hands-free loop cannot run — no recogniser, a turn that
  /// was not heard, or a learner who would rather type.
  Widget _voiceControls(AppStrings s, Color color) {
    final phase = _voice;

    if (!_voiceMode || phase == _VoicePhase.unavailable) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            phase == _VoicePhase.unavailable
                ? s.microphoneUnavailable
                : s.yourTurn,
            style: context.text.bodySmall?.copyWith(
              color: context.colors.onSurface.withValues(alpha: 0.55),
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: _chatInput,
                  decoration: InputDecoration(hintText: s.yourTurn),
                  onSubmitted: (_) => _sendChat(),
                ),
              ),
              const SizedBox(width: AppSpacing.xs),
              IconButton.filled(
                onPressed: _busy ? null : () => _sendChat(),
                icon: const Icon(Icons.send_rounded),
              ),
            ],
          ),
          // Only offered when the device can actually listen — a button that
          // cannot work is worse than no button.
          if (phase != _VoicePhase.unavailable)
            TextButton.icon(
              onPressed: () => setState(() {
                _voiceMode = true;
                _voice = _VoicePhase.idle;
              }),
              icon: const Icon(Icons.mic_rounded, size: 18),
              label: Text(s.useVoice),
            ),
        ],
      );
    }

    final (label, icon, active) = switch (phase) {
      _VoicePhase.speaking => (s.tutorSpeaking, Icons.volume_up_rounded, true),
      // The label is an instruction while listening: the learner needs to know
      // that nothing is waiting on a pause, and that finishing is their move.
      _VoicePhase.listening => (s.tapWhenDone, Icons.stop_rounded, true),
      _VoicePhase.thinking => (s.thinking, Icons.more_horiz_rounded, true),
      _ => (s.tapToSpeak, Icons.mic_none_rounded, false),
    };

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // What the recogniser has heard so far, so the learner can see they are
        // being picked up while they talk.
        if (_heard.isNotEmpty)
          Padding(
            padding: const EdgeInsets.only(bottom: AppSpacing.sm),
            child: Text(
              _heard,
              style: context.text.bodyMedium?.copyWith(
                color: context.colors.onSurface.withValues(alpha: 0.7),
                fontStyle: FontStyle.italic,
              ),
            ),
          ),
        Row(
          children: [
            _VoiceIndicator(
              icon: icon,
              color: color,
              active: active,
              listening: phase == _VoicePhase.listening,
              // The whole control: tap to start talking, tap again when
              // finished. Nothing decides that for the learner.
              onTap: switch (phase) {
                _VoicePhase.idle => () => unawaited(_startListening()),
                _VoicePhase.listening => () => unawaited(_stopAndSend()),
                _ => null,
              },
            ),
            const SizedBox(width: AppSpacing.md),
            Expanded(
              child: Text(label, style: context.text.titleSmall),
            ),
            // Only while there is something to throw away. A bin beside an
            // idle microphone offers to delete nothing, and a learner reading
            // it wonders what they are about to lose.
            if (phase == _VoicePhase.listening)
              IconButton(
                onPressed: () => unawaited(_discardListening()),
                tooltip: s.discardRecording,
                icon: const Icon(Icons.delete_outline_rounded),
                color: context.palette.danger,
              ),
          ],
        ),
        const SizedBox(height: AppSpacing.xs),
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: TextButton.icon(
            onPressed: () async {
              await _mic.cancel();
              await _speech.stop();
              if (mounted) {
                setState(() {
                  _voiceMode = false;
                  _voice = _VoicePhase.idle;
                });
              }
            },
            icon: const Icon(Icons.keyboard_rounded, size: 18),
            label: Text(s.typeInstead),
          ),
        ),
      ],
    );
  }
}

/// Where the hands-free conversation is.
///
/// A single value rather than a set of booleans: the states are mutually
/// exclusive, and "speaking and listening at once" is precisely the bug that
/// makes a voice loop record itself.
enum _VoicePhase {
  /// Waiting for the learner to start it — after a failed recognition, or when
  /// they have chosen to type.
  idle,

  /// The tutor is talking. The microphone stays shut.
  speaking,

  /// The microphone is open and the learner is talking.
  listening,

  /// The turn is with the server.
  thinking,

  /// This device cannot listen; typing is offered instead.
  unavailable,
}

/// A single dot that shows, at a glance, whether the app is talking,
/// listening, or thinking — and pulses while the microphone is open so the
/// learner can tell they are being heard.
class _VoiceIndicator extends StatelessWidget {
  const _VoiceIndicator({
    required this.icon,
    required this.color,
    required this.active,
    required this.listening,
    this.onTap,
  });

  final IconData icon;
  final Color color;
  final bool active;
  final bool listening;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: onTap != null,
      // Labelled so the control can be found by what it is rather than by the
      // icon it happens to be showing, which changes with the phase.
      label: 'voice',
      child: GestureDetector(
        onTap: onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 250),
          width: 56,
          height: 56,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: active
                ? color.withValues(alpha: listening ? 0.22 : 0.12)
                : context.palette.subtleSurface,
            border: Border.all(
              color: active ? color : context.palette.border,
              width: listening ? 2.5 : 1,
            ),
          ),
          child: Icon(icon, color: active ? color : context.colors.onSurface),
        ),
      ),
    );
  }
}

class _ChatMessage {
  const _ChatMessage(this.text, {required this.fromAi});

  final String text;
  final bool fromAi;
}

class _ChatBubble extends StatelessWidget {
  const _ChatBubble({required this.message, required this.color});

  final _ChatMessage message;
  final Color color;

  @override
  Widget build(BuildContext context) {
    final fromAi = message.fromAi;
    return Align(
      alignment:
          fromAi ? AlignmentDirectional.centerStart : AlignmentDirectional.centerEnd,
      child: Container(
        margin: const EdgeInsets.only(bottom: AppSpacing.xs),
        padding: const EdgeInsets.symmetric(
          horizontal: AppSpacing.md,
          vertical: AppSpacing.sm,
        ),
        constraints: const BoxConstraints(maxWidth: 320),
        decoration: BoxDecoration(
          color: fromAi
              ? context.colors.surface
              : color.withValues(alpha: 0.14),
          borderRadius: BorderRadius.only(
            topLeft: AppRadii.md,
            topRight: AppRadii.md,
            bottomLeft: fromAi ? Radius.zero : AppRadii.md,
            bottomRight: fromAi ? AppRadii.md : Radius.zero,
          ),
          border: Border.all(
            color: fromAi ? context.palette.border : color.withValues(alpha: 0.3),
          ),
        ),
        child: Text(message.text, style: context.text.bodyMedium),
      ),
    );
  }
}

class _LetterTile extends StatelessWidget {
  const _LetterTile({
    required this.letter,
    required this.used,
    required this.onTap,
  });

  final String letter;
  final bool used;
  final VoidCallback? onTap;

  /// A space is a tile like any other, and needs to look like one: an empty
  /// square reads as a rendering fault, and a learner spelling "alarm clock"
  /// has to see what they are tapping.
  ///
  /// It is shaped like a keyboard's space bar rather than a letter square —
  /// three tiles wide — because that is the shape a learner already knows
  /// means "space", and a square carrying an icon does not. Everything else
  /// about it is unchanged: one tap places it exactly as a letter does.
  bool get _isSpace => letter.trim().isEmpty;

  @override
  Widget build(BuildContext context) {
    return Opacity(
      opacity: used ? 0.3 : 1,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: used ? null : onTap,
          borderRadius: AppRadii.chipBorder,
          child: Container(
            // Three letter tiles and the two gaps between them, so the bar
            // lines up with the rest of the pool instead of floating.
            width: _isSpace ? 46 * 3 + AppSpacing.xs * 2 : 46,
            height: 52,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: context.colors.surface,
              borderRadius: AppRadii.chipBorder,
              border: Border.all(color: context.palette.border),
            ),
            child: _isSpace
                ? Icon(
                    Icons.space_bar_rounded,
                    size: 20,
                    color: context.colors.onSurface.withValues(alpha: 0.55),
                  )
                : Text(letter, style: context.text.titleLarge),
          ),
        ),
      ),
    );
  }
}

class _FeedbackBanner extends StatelessWidget {
  const _FeedbackBanner({
    required this.correct,
    required this.title,
    this.body,
  });

  final bool correct;
  final String title;
  final String? body;

  @override
  Widget build(BuildContext context) {
    final color = correct ? context.palette.success : context.palette.danger;
    final background =
        correct ? context.palette.successSurface : context.palette.dangerSurface;

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(AppSpacing.sm),
      decoration: BoxDecoration(
        color: background,
        borderRadius: AppRadii.fieldBorder,
        border: Border.all(color: color.withValues(alpha: 0.4)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(
            correct ? Icons.check_circle_rounded : Icons.info_outline_rounded,
            color: color,
            size: 20,
          ),
          const SizedBox(width: AppSpacing.xs),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: context.text.titleSmall?.copyWith(color: color),
                ),
                if (body != null) ...[
                  const SizedBox(height: 2),
                  Text(body!, style: context.text.bodySmall),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// Listening playback with the normal/slow support the documents describe —
/// slow speed is an accessibility aid, never part of the score.
class _ListeningPlayer extends ConsumerStatefulWidget {
  const _ListeningPlayer({required this.text, required this.color});

  final String text;
  final Color color;

  @override
  ConsumerState<_ListeningPlayer> createState() => _ListeningPlayerState();
}

class _ListeningPlayerState extends ConsumerState<_ListeningPlayer> {
  bool _slow = false;
  bool _played = false;
  bool _audioFailed = false;

  /// One id for the whole clip, so switching speed replaces the utterance
  /// rather than lighting up a second control.
  String get _id => 'listening:${widget.text.hashCode}';

  @override
  void initState() {
    super.initState();
    // The clip starts on its own (§22). A listening exercise where the first
    // action is "press play" wastes the learner's first interaction on
    // something the screen already knows it needs to do.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) unawaited(_play());
    });
  }

  Future<void> _play() async {
    setState(() => _played = true);
    final ok = await ref.read(speechServiceProvider).speak(
          _id,
          widget.text,
          rate: _slow ? SpeechRate.slow : SpeechRate.normal,
        );
    if (mounted && !ok) setState(() => _audioFailed = true);
  }

  /// Play, or stop what is already playing (§23).
  Future<void> _toggle() async {
    final speech = ref.read(speechServiceProvider);
    if (speech.isSpeakingId(_id)) {
      await speech.stop();
      return;
    }
    await _play();
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    // Read from the service, never from a local flag: when the clip ends by
    // itself the control has to return to "replay" without being told.
    final playing = ref.watch(speechServiceProvider).isSpeakingId(_id);

    return AppCard(
      color: widget.color.withValues(alpha: 0.06),
      borderColor: widget.color.withValues(alpha: 0.3),
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.md,
        vertical: AppSpacing.lg,
      ),
      child: Column(
        children: [
          Container(
            width: 96,
            height: 96,
            decoration: BoxDecoration(
              color: widget.color.withValues(alpha: 0.14),
              shape: BoxShape.circle,
            ),
            child: IconButton(
              iconSize: 44,
              color: widget.color,
              onPressed: _toggle,
              icon: Icon(
                playing
                    ? Icons.stop_rounded
                    : _played
                        ? Icons.replay_rounded
                        : Icons.play_arrow_rounded,
              ),
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          Text(
            playing
                ? s.stopAudio
                : _played
                    ? s.playAgain
                    : s.playAudio,
            style: context.text.titleSmall,
          ),
          const SizedBox(height: AppSpacing.md),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(s.normalSpeed)),
              ButtonSegment(value: true, label: Text(s.slowSpeed)),
            ],
            selected: {_slow},
            onSelectionChanged: (value) {
              setState(() => _slow = value.first);
              _play();
            },
          ),
          // A device that cannot speak must not block the whole session: the
          // transcript is revealed early rather than after the test
          // (demo review §51).
          if (_audioFailed) ...[
            const SizedBox(height: AppSpacing.md),
            AppCard(
              color: context.palette.warningSurface,
              borderColor: context.palette.warning.withValues(alpha: 0.35),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(Icons.volume_off_rounded,
                          size: 18, color: context.palette.warning),
                      const SizedBox(width: AppSpacing.xs),
                      Expanded(
                        child: Text(s.audioUnavailable,
                            style: context.text.labelMedium),
                      ),
                    ],
                  ),
                  const SizedBox(height: AppSpacing.xs),
                  // The script is English: it must not inherit the Arabic
                  // interface's direction, exactly as the passage does not.
                  EnglishText(widget.text, style: context.text.bodyMedium),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
