import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/analytics/analytics_tracker.dart';
import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/audio/speech_recognition_service.dart';
import '../../core/audio/speech_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import '../../core/widgets/speaker_button.dart';
import '../auth/session_controller.dart';
import 'clip_playback.dart';
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

  /// The conversation's list, kept at its newest line as lines arrive — a
  /// message sent and not seen until the reply pushes it into view is not a
  /// conversation.
  final ScrollController _chatScroll = ScrollController();

  /// A fresh conversation's opening line, held back until its voice is ready
  /// so the two arrive together (ADR-116).
  String? _heldOpening;
  final List<_ChatMessage> _chat = [];
  final List<String> _tiles = [];

  SkillSession? _session;
  SessionResult? _result;
  ApiException? _error;

  bool _loading = true;
  bool _busy = false;
  bool _contentDone = false;

  /// True while a Reading learner is looking at the passage again *after* the
  /// questions have begun. It is a view, not a phase: [_contentDone] stays
  /// true, so nothing about the session's progress changes and the level stays
  /// locked — going back to the text must not throw away answers already given.
  bool _passageRevisit = false;
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

  /// A recorded turn is on its way to the server to be written down
  /// (ADR-107) — distinct from the tutor thinking about its reply.
  bool _transcribing = false;
  late final SpeechRecognitionService _mic;

  /// On-screen behaviour for the admin area (ADR-125). Captured in
  /// [initState] for the same reason as [_api]: [dispose] reports the screen
  /// closing, and `ref` is gone by then.
  late final AnalyticsTracker _tracker;

  /// How long the screen has been open — Learning Time is measured from it.
  final Stopwatch _screenClock = Stopwatch()..start();

  /// Time on the current question, from it appearing to the answer leaving.
  final Stopwatch _itemClock = Stopwatch()..start();

  /// When the Writing feedback appeared, to measure how long it was read.
  DateTime? _feedbackShownAt;

  void _restartItemClock() => _itemClock
    ..reset()
    ..start();

  void _track(String name, {String? wordId, int? durationMs, Map<String, Object?>? props}) =>
      _tracker.track(
        name,
        sessionId: _session?.id,
        wordId: wordId,
        skill: widget.skill,
        durationMs: durationMs,
        level: widget.skill == SkillType.spelling ? null : _session?.levelUsed.wire,
        screen: 'session',
        props: props,
      );

  @override
  void initState() {
    super.initState();
    _api = ref.read(wordOsApiProvider);
    _speech = ref.read(speechServiceProvider);
    _mic = ref.read(speechRecognitionProvider);
    _tracker = ref.read(analyticsTrackerProvider);
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
    // How long the lesson was in front of the learner, and whether they left
    // it unfinished — the two things only this screen can see (ADR-125).
    if (_session != null) {
      if (_result == null) {
        _track(ClientEvents.exerciseExited, props: {
          'answered': _progress?.answered ?? 0,
          'remaining': _progress?.remaining ?? 0,
        });
      }
      _track(ClientEvents.screenLeft, durationMs: _screenClock.elapsedMilliseconds);
    }
    unawaited(_tracker.flush());
    _freeText.dispose();
    _chatInput.dispose();
    _chatScroll.dispose();
    super.dispose();
  }

  bool get _needsContentPhase =>
      widget.skill == SkillType.reading || widget.skill == SkillType.listening;

  /// Whether the passage — or the recording — can be reopened from the
  /// questions.
  ///
  /// A learner who tapped "I finished reading" too early, or who wants to
  /// check a detail the question is asking about, should not have to abandon
  /// the session to get back to it.
  ///
  /// Listening was excluded at first, on the argument that replaying the clip
  /// during the questions hands over the answers. The product owner asked for
  /// it anyway, and they are right about which is worse: a comprehension
  /// question you cannot re-listen to is a memory test, and this section is
  /// not measuring memory (ADR-068). The clip is the same one, and going back
  /// to it changes nothing about the session.
  bool get _canRevisitPassage =>
      _needsContentPhase &&
      _result == null &&
      _contentDone &&
      !_passageRevisit &&
      _session?.content != null;

  /// Reading and Listening answer in two steps — choose an option, then check
  /// it — because a single tap used to be the whole answer, and a mis-tap
  /// therefore spent the attempt before the learner had decided anything.
  bool get _twoStepAnswer =>
      widget.skill == SkillType.reading || widget.skill == SkillType.listening;

  /// Whether this question ends in an explicit "check", or whether the foot of
  /// the screen goes straight to "next".
  ///
  /// Spelling keeps its own control: with letter tiles it sits in the same row
  /// as undo and clear, and it belongs beside the tiles it commits.
  bool _hasCheckStep(SessionItem item) =>
      _twoStepAnswer || item.type == SessionItemType.writingTask;

  /// What "check" does for this question, or null while there is nothing yet
  /// to check — an unchosen option, or an empty box.
  VoidCallback? _checkAction(SessionItem item) {
    if (_busy || _answered) return null;

    if (item.type == SessionItemType.writingTask) {
      // `_submitWriting` refuses an empty sentence anyway; refusing it here as
      // well is what stops the button looking pressable when it is not.
      return _freeText.text.trim().isEmpty ? null : () => _submitWriting(item);
    }

    final chosen = _selectedOption;
    return chosen == null ? null : () => _answer(item, chosen);
  }

  SessionItem? get _currentItem {
    final session = _session;
    final id = _currentItemId;
    if (session == null || id == null) return null;
    return session.items.where((i) => i.id == id).firstOrNull;
  }

  bool get _answered => _lastAnswer != null || _lastWriting != null;

  /// The target word's surface form, so the context passage can highlight it.
  /// Fetches the voice of everything this session will say, in the order it
  /// will be heard, starting from the question the learner is on (ADR-110).
  ///
  /// So play answers at once, on a slow connection too. The audio arrives in
  /// the background while the learner reads and answers — the first
  /// question's first, then the next question's — rather than on each press.
  /// Only the phone's voice is asked for nothing: [SpeechService.prepare] is
  /// a no-op there.
  void _prepareVoice(SkillSession session, String? fromItemId) {
    final speech = ref.read(speechServiceProvider);
    final items = session.items;
    final from = items.indexWhere((i) => i.id == fromItemId);
    final ordered = from <= 0
        ? items
        : [...items.sublist(from), ...items.sublist(0, from)];

    for (final item in ordered) {
      final sentence = item.audioText;
      if (sentence != null && sentence.trim().isNotEmpty) {
        speech.prepare('sentence:${sentence.hashCode}', sentence);
      }
      // The word button under a Reading or Listening word question
      // (_multipleChoice).
      if (_offersWordAudio(item)) {
        final word = _targetTextFor(item);
        if (word != null && word.trim().isNotEmpty) {
          speech.prepare('pronounce:$word', word);
        }
      }
    }
    // Speaking opens on these, before the conversation.
    for (final word in session.warmup) {
      speech.prepare('warmup:${word.wordId}', word.text);
    }
  }

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
  ///
  /// Which language it is said in is the *session's* answer, not the app's: a
  /// Writing task from B1 up is set in English (ADR-088). Applied here, to the
  /// instruction alone, so it cannot leak into the buttons and headings around
  /// it — those are the app talking, and the app still speaks the learner's
  /// language.
  String _instruction(AppStrings s, SessionItem item) {
    if (item.promptKey == null) return item.prompt;

    final said = s.forInstructions(_session?.instructionLanguage);
    return said.sessionPrompt(item.promptKey, _targetTextFor(item) ?? '');
  }

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
      final session = await _api.startSession(
        widget.skill,
        practice: _practice,
      );
      if (!mounted) return;

      // Where to continue is the server's answer, not `items.first` — on a
      // resumed session the early items are already cleared, and a requeued one
      // may be waiting at the back of the queue.
      final progress =
          session.progress ??
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
            // Held, not shown: it appears with its voice (ADR-116).
            _heldOpening = session.conversation!.opening;
          } else {
            _chat.addAll(
              turns.map((t) => _ChatMessage(t.text, fromAi: t.fromAi)),
            );
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
      // After this frame, so a passage the screen opens on — which asks for
      // its own voice as it is built — is fetched ahead of the questions.
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _prepareVoice(session, progress.nextItemId);
      });

      // The conversation starts speaking on its own — but only once the
      // learner has seen which words it is about (§26). A resumed conversation
      // has been briefed already and simply carries on.
      if (widget.skill == SkillType.speaking &&
          (_chat.isNotEmpty || _heldOpening != null)) {
        // Fetched now, so a learner who spends a minute on the warm-up words
        // does not then wait for the greeting's voice as well.
        final opening = _heldOpening;
        if (opening != null) _speech.prepare(_tutorId(opening), opening);
        if (_speakingBriefed) _resumeSpeaking();
        _scrollToEnd();
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

  /// Picks the conversation back up from whatever the tutor last said — or,
  /// for a conversation that has not started, opens it.
  void _resumeSpeaking() {
    final opening = _heldOpening;
    if (opening != null) {
      unawaited(_openConversation(opening));
      return;
    }
    final lastFromAi = _chat.isNotEmpty && _chat.last.fromAi
        ? _chat.last.text
        : null;
    if (lastFromAi != null) unawaited(_speakThenListen(lastFromAi));
  }

  String _tutorId(String line) => 'tutor:${line.hashCode}';

  /// The greeting, shown and spoken together.
  Future<void> _openConversation(String opening) async {
    if (mounted) setState(() => _voice = _VoicePhase.thinking);
    await _showTutorLine(opening, spoken: true);
    if (!mounted) return;
    setState(() => _heldOpening = null);
    await _speakThenListen(opening);
  }

  /// Adds one of the tutor's lines to the conversation — once its voice is
  /// ready, when it is going to be spoken (ADR-116).
  ///
  /// The product owner's report: the text arrived and the voice followed
  /// seconds later, so the learner read the line in silence and then heard
  /// it again. A tutor who speaks is heard and read at the same moment. The
  /// wait is bounded by the voice's own limits: if the server's voice fails
  /// or is late, the phone says the line, and the text appears with that.
  Future<void> _showTutorLine(String line, {required bool spoken}) async {
    if (spoken) await _speech.ready(_tutorId(line), line);
    if (!mounted) return;
    setState(() => _chat.add(_ChatMessage(line, fromAi: true)));
    _scrollToEnd();
  }

  /// Brings the newest line into view once it has been laid out.
  void _scrollToEnd() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_chatScroll.hasClients) return;
      unawaited(_chatScroll.animateTo(
        _chatScroll.position.maxScrollExtent,
        duration: const Duration(milliseconds: 250),
        curve: Curves.easeOut,
      ));
    });
  }

  Future<void> _answer(SessionItem item, String answer) async {
    if (_answered || _busy) return;
    setState(() => _busy = true);
    try {
      final result = await _api.submitAnswer(
        sessionId: _session!.id,
        itemId: item.id,
        answer: answer,
        timeMs: _itemClock.elapsedMilliseconds,
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
        timeMs: _itemClock.elapsedMilliseconds,
      );
      if (mounted) {
        _feedbackShownAt = DateTime.now();
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

    await _speech.speakToCompletion(_tutorId(text), text);
    if (!mounted || _speakingFinished) return;

    // A device that cannot listen falls back to typing rather than showing a
    // microphone that will never work.
    final canListen = await _mic.initialise();
    if (!mounted) return;

    setState(
      () => _voice = canListen ? _VoicePhase.idle : _VoicePhase.unavailable,
    );
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
      // A turn is timed from the moment the learner is handed the floor.
      _restartItemClock();
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

  /// Closes the microphone and hands the words back to the learner to check.
  ///
  /// Not sent yet. A recogniser mishears — a name, a number, the one word the
  /// whole sentence turned on — and until now the only exits from a recording
  /// were "send it as heard" and "throw it away". Reading it first, and fixing
  /// it if it is wrong, is what a person does with dictation everywhere else
  /// (ADR-069).
  Future<void> _stopAndReview() async {
    if (!mounted || _voice != _VoicePhase.listening) return;

    setState(() {
      _voice = _VoicePhase.thinking;
      _transcribing = true;
    });
    // Never throws: a failure comes back as null with `lastFailure` set.
    final said = await _mic.stopAndRead();
    _transcribing = false;
    if (!mounted) return;

    if (said == null || said.trim().isEmpty) {
      // Nothing usable. The turn is offered again rather than reviewed — there
      // is nothing to review.
      setState(() {
        _voice = _VoicePhase.idle;
        _heard = '';
      });
      // The server could not listen (ADR-107) — said so, rather than left
      // looking like the learner said nothing.
      if (_mic.lastFailure != null) _snack(_s.couldNotListen);
      return;
    }

    setState(() {
      _voice = _VoicePhase.reviewing;
      _heard = said;
      // The same box the typed answer uses, so editing is a normal text field
      // rather than a special mode.
      _chatInput.text = said;
      _chatInput.selection = TextSelection.collapsed(
        offset: _chatInput.text.length,
      );
    });
  }

  /// Sends what the learner has read over, and edited if they wanted to.
  Future<void> _sendReviewed() async {
    if (!mounted || _voice != _VoicePhase.reviewing) return;
    final text = _chatInput.text.trim();
    if (text.isEmpty) return;
    await _sendChat(text);
  }

  /// Drops the reviewed words and opens the microphone again.
  Future<void> _recordAgain() async {
    if (!mounted) return;
    setState(() {
      _heard = '';
      _chatInput.clear();
      _voice = _VoicePhase.idle;
    });
    await _startListening();
  }

  /// Sends one learner turn, from voice or from the keyboard.
  Future<void> _sendChat([String? spoken]) async {
    final text = (spoken ?? _chatInput.text).trim();
    if (text.isEmpty || _busy) return;

    // Sent the moment it is sent: their line joins the conversation and the
    // box empties at once, the way every messenger behaves.
    final sentMessage = _ChatMessage(text, fromAi: false);
    setState(() {
      _chat.add(sentMessage);
      _chatInput.clear();
      _heard = '';
      _busy = true;
      _voice = _VoicePhase.thinking;
    });
    _scrollToEnd();

    try {
      final turn = await _api.submitSpeakingTurn(
        sessionId: _session!.id,
        transcript: text,
        timeMs: _itemClock.elapsedMilliseconds,
      );
      if (!mounted) return;

      // Shown with its voice, if it is going to be spoken (ADR-116). The
      // last line always is; the others when the learner is talking rather
      // than typing.
      await _showTutorLine(turn.aiMessage,
          spoken: turn.isFinal || _voiceMode);
      if (!mounted) return;
      setState(() {
        _speakingFinished = turn.isFinal;
        _busy = false;
      });

      if (turn.isFinal) {
        // The server ended the conversation. The last reply is still spoken —
        // being cut off mid-goodbye is worse than waiting a moment for the
        // result — and only then is the session completed and evaluated.
        setState(() => _voice = _VoicePhase.speaking);
        await _speech.speakToCompletion(
          _tutorId(turn.aiMessage),
          turn.aiMessage,
        );
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
      // Not sent, so not left looking sent: the line comes back out of the
      // conversation and into the box, ready to send again — never lost.
      if (mounted) {
        setState(() {
          if (_chat.isNotEmpty && identical(_chat.last, sentMessage)) {
            _chat.removeLast();
          }
          _chatInput.text = text;
          _chatInput.selection =
              TextSelection.collapsed(offset: _chatInput.text.length);
          _voice = _voiceMode ? _VoicePhase.reviewing : _VoicePhase.idle;
        });
      }
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
    final shown = _feedbackShownAt;
    if (_lastWriting != null && shown != null) {
      _track(ClientEvents.feedbackViewed,
          wordId: _currentItem?.wordId,
          durationMs: DateTime.now().difference(shown).inMilliseconds,
          props: {'passed': _lastWriting!.passed});
    }
    _feedbackShownAt = null;
    _restartItemClock();
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
      final result = await _api.completeSession(_session!.id);
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
    ScaffoldMessenger.of(
      context,
    ).showSnackBar(SnackBar(content: Text(message)));
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
          if (_canRevisitPassage)
            IconButton(
              onPressed: _busy
                  ? null
                  : () => setState(() => _passageRevisit = true),
              icon: Icon(
                widget.skill == SkillType.listening
                    ? Icons.headphones_rounded
                    : Icons.menu_book_rounded,
              ),
              tooltip: widget.skill == SkillType.listening
                  ? s.showRecording
                  : s.showPassage,
            ),
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
      SkillType.reading || SkillType.listening =>
        !_contentDone && (session.content?.canChangeLevel ?? false),
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
                AppSpacing.md,
                0,
                AppSpacing.md,
                AppSpacing.xs,
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    s.changeLevelTitle,
                    style: sheetContext.text.titleMedium,
                  ),
                  const SizedBox(height: AppSpacing.xxs),
                  Text(
                    widget.skill == SkillType.speaking
                        ? s.changeLevelHintSpeaking
                        : s.changeLevelHint,
                    style: sheetContext.text.bodySmall?.copyWith(
                      color: sheetContext.colors.onSurface.withValues(
                        alpha: 0.7,
                      ),
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
                      onSelected: (_) => Navigator.of(sheetContext).pop(level),
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
          _currentItemId = session.items.isEmpty
              ? null
              : session.items.first.id;
        }
      });

      // The level the learner just chose is now their level for this skill, on
      // the server. Settings and the hub both render it from the cached
      // profile, so without this they keep showing the old band until the next
      // sign-in — the learner changes it here and finds it unchanged there.
      await ref.read(sessionProvider.notifier).refresh();
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
        final canPractise =
            widget.skill == SkillType.reading ||
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
      return ErrorView.from(
        _error!,
        s,
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
      // A passage being written or re-told, not an answer being marked.
      return BusyView(message: s.writingPassage);
    }

    if (widget.skill == SkillType.speaking) return _speakingView(s, color);

    // The passage wins whenever the learner has asked for it back — the
    // questions are still there, untouched, behind it.
    final body = !_contentDone || _passageRevisit
        ? _contentView(s, color)
        : _questionView(s, color);
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
                // The passage's own title, written with it. Prose that starts
                // with no heading reads as an extract torn out of something
                // else — every reading task in a classroom or an exam is
                // titled, and the title is what tells the learner what they
                // are about to read about (ADR-066).
                //
                // English content, so it is pinned left-to-right like the
                // passage below it rather than following the Arabic interface.
                // Shown for a listening clip as well: it names what is about
                // to be heard without giving any of it away, which is exactly
                // what a title does at the top of an exam's listening section.
                if (content.title != null) ...[
                  const SizedBox(height: AppSpacing.sm),
                  EnglishText(
                    content.title!,
                    style: context.text.headlineSmall?.copyWith(height: 1.3),
                  ),
                ],
                const SizedBox(height: AppSpacing.md),
                if (isListening)
                  _ListeningPlayer(
                    text: content.text,
                    color: color,
                    onAudio: (event) => _track(event),
                  )
                else
                  AppCard(
                    child: HighlightedPassage(
                      content: content,
                      color: color,
                      onWordTap: (word, {required isTarget}) {
                        _track(ClientEvents.translationOpened,
                            props: {'word': word, 'isTarget': isTarget});
                        showWordLookup(
                        context,
                        word: word,
                        isTarget: isTarget,
                        color: color,
                        // Which passage the tap happened in, so adding the word
                        // can ask the server what it meant *here* (ADR-073).
                        sessionId: session.id,
                        // The meaning the generator gave this word in this
                        // sentence. Null falls back to the dictionary, which
                        // can only offer every sense the word has ever had.
                        inContext: content.glossaryFor(word),
                      );
                      },
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
              // A revisit only closes the passage again: the questions were
              // never left, so there is no progress to move on.
              setState(() {
                if (_passageRevisit) {
                  _passageRevisit = false;
                } else {
                  _contentDone = true;
                  // The first question's clock starts when it appears, not
                  // when the passage did.
                  _restartItemClock();
                }
              });
            },
            child: Text(
              _passageRevisit
                  ? s.backToQuestions
                  : isListening
                  ? s.iFinishedListening
                  : s.iFinishedReading,
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
          // One question, one button: it reads "check" until the verdict is on
          // screen and "next" afterwards (ADR-095).
          //
          // Reading and Listening already worked this way — a tap on an option
          // is only a choice, so a mis-tap does not spend the attempt. Writing
          // did not: it carried its own "check" inside the scrolling body while
          // this foot of the screen held a disabled "next", which is two
          // buttons for one decision with the live one further from the thumb
          // than the dead one.
          //
          // Rebuilt as the learner types, because for a written answer the
          // question of whether there is anything to check is the text field's
          // to answer.
          child: ValueListenableBuilder(
            valueListenable: _freeText,
            builder: (context, _, _) => _hasCheckStep(item) && !_answered
                ? FilledButton(
                    onPressed: _checkAction(item),
                    child: Text(_busy ? s.evaluating : s.checkAnswer),
                  )
                : FilledButton(
                    onPressed: _answered && !_busy ? _next : null,
                    child: Text(
                      _progress?.nextItemId == null ? s.finish : s.next,
                    ),
                  ),
          ),
        ),
      ],
    );
  }

  /// Whether a question offers its word to be heard, at both speeds.
  ///
  /// Only the questions about a word: the comprehension questions have no word
  /// of their own (ADR-081). Reading as well as Listening (ADR-119): the
  /// product owner asked to hear the word Reading is asking about, normally
  /// and slowly — seeing a word spelled is not knowing how it sounds.
  bool _offersWordAudio(SessionItem item) =>
      (widget.skill == SkillType.listening ||
          widget.skill == SkillType.reading) &&
      item.type == SessionItemType.targetWord;

  Widget _multipleChoice(AppStrings s, SessionItem item, Color color) {
    final result = _lastAnswer;
    final pronounce = _offersWordAudio(item) ? _targetTextFor(item) : null;

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
        // Through [_instruction], so a question that arrives as a *key* is
        // said in the learner's own language — which is how Listening's word
        // question avoids naming the word at all (ADR-035, ADR-085). A
        // comprehension question has no key and is shown as written.
        //
        // Direction follows the text: rendered in the Arabic interface's
        // direction, the question mark of `What does "fan" mean here?` moved
        // to the front of the sentence.
        // Reading: the speakers ride at the end of the question itself, in
        // its line (ADR-119). Beside it as a separate column they took the
        // width the question needed, so it broke onto two lines and pushed
        // the last option below the fold; inline, they sit level with the
        // words and only wrap when the question would have anyway.
        if (widget.skill == SkillType.reading &&
            pronounce != null &&
            pronounce.trim().isNotEmpty)
          _QuestionWithSpeakers(
            question: _instruction(s, item),
            word: pronounce,
            color: color,
          )
        else
          AutoDirectionText(
            _instruction(s, item),
            style:
                context.text.titleMedium?.copyWith(fontSize: 18, height: 1.45),
          ),
        // Listening: under the question, above the options — the learner has
        // just been asked about a word they have only heard, and this is the
        // moment they want to hear it again. Never spelled (ADR-085).
        if (widget.skill == SkillType.listening &&
            pronounce != null &&
            pronounce.trim().isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          WordPronunciation(word: pronounce, color: color),
        ],
        const SizedBox(height: AppSpacing.md),
        for (final option in item.options)
          OptionTile(
            label: option,
            enabled: result == null && !_busy,
            // The pending choice, before it is checked — marked so the learner
            // can see what they picked and change it.
            selected: result == null && _selectedOption == option,
            correct: result == null
                ? null
                : option == result.correctAnswer
                ? true
                : (_selectedOption == option ? false : null),
            onTap: () {
              // A tap is only a choice here; the answer goes to the server when
              // the learner presses "check" (see the footer).
              if (_twoStepAnswer) {
                setState(() => _selectedOption = option);
                return;
              }
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
        // Direction follows the instruction's own language, not the
        // interface's: an English task set in the Arabic app would otherwise
        // be laid out right-to-left and lose its full stop to the front.
        AutoDirectionText(
          _instruction(s, item),
          style: context.text.titleMedium,
        ),
        const SizedBox(height: AppSpacing.md),
        TextField(
          controller: _freeText,
          maxLines: 4,
          enabled: evaluation == null,
          decoration: InputDecoration(hintText: s.writeSentence),
        ),
        // No button here: the one at the foot of the screen marks this answer
        // and then moves on (ADR-095).
        if (evaluation != null) ...[
          const SizedBox(height: AppSpacing.md),
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
                  Icon(
                    Icons.lightbulb_outline_rounded,
                    size: 18,
                    color: context.palette.warning,
                  ),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          s.atYourLevel(_session!.levelUsed),
                          style: context.text.labelSmall?.copyWith(
                            color: context.palette.warning,
                          ),
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

  /// Spelling is always assembled from letters — there is no typed variant.
  ///
  /// A text field on a phone raises the keyboard, and the keyboard autocorrects,
  /// predicts and completes: the learner taps a suggestion and the exercise has
  /// measured the keyboard rather than them (ADR-100). The difficulty lives in
  /// the hint ladder instead, which already starts at the rung that suits the
  /// level.
  Widget _spellingTask(AppStrings s, SessionItem item, Color color) {
    final result = _lastAnswer;

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
                textDirection: item.clueKind == SpellingClueKind.arabicMeaning
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
                  Icon(
                    Icons.lightbulb_outline_rounded,
                    size: 18,
                    color: context.palette.warning,
                  ),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          s.spellingClueLabel(hint.kind),
                          style: context.text.labelSmall?.copyWith(
                            color: context.palette.warning,
                          ),
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
              onPressed: () {
                _track(ClientEvents.hintUsed,
                    wordId: item.wordId, props: {'step': _hintStep + 1});
                setState(() => _hintStep++);
              },
              icon: const Icon(Icons.lightbulb_outline_rounded, size: 18),
              label: Text(_hintStep == 0 ? s.showHint : s.easierHint),
            ),
          ],
        ],
        const SizedBox(height: AppSpacing.lg),
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
                      WordSpeakerButtons(
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
      await Future<void>.delayed(
        Duration(milliseconds: result.isCorrect ? 550 : 1800),
      );
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
        ScaffoldMessenger.of(
          context,
        ).showSnackBar(SnackBar(content: Text(_s.apiError(e.code, e.message))));
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
            controller: _chatScroll,
            padding: const EdgeInsets.all(AppSpacing.md),
            // One more while the tutor is preparing a line: the dots a
            // messenger shows, so a reply being written is visible where the
            // reply will appear.
            itemCount: _chat.length + (_tutorPreparing ? 1 : 0),
            itemBuilder: (context, index) => index < _chat.length
                ? _ChatBubble(message: _chat[index], color: color)
                : const _TypingBubble(),
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

  /// The tutor is writing, or fetching the voice for, its next line — and
  /// not the recogniser writing down the learner's (ADR-107).
  bool get _tutorPreparing =>
      _voice == _VoicePhase.thinking && !_transcribing;

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
                // Grows with what they type, up to a few lines, instead of
                // scrolling one line sideways out of sight. English, so left
                // to right whatever the interface is.
                child: TextField(
                  controller: _chatInput,
                  minLines: 1,
                  maxLines: 4,
                  textDirection: TextDirection.ltr,
                  textInputAction: TextInputAction.send,
                  decoration: InputDecoration(
                    hintText: s.yourTurn,
                    hintTextDirection: Directionality.of(context),
                  ),
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
      _VoicePhase.reviewing => (
        s.checkBeforeSending,
        Icons.edit_rounded,
        false,
      ),
      // After a recorded turn this is the server writing it down (ADR-107);
      // "Thinking…" read as though the tutor were already answering.
      _VoicePhase.thinking => (
        _transcribing ? s.transcribing : s.thinking,
        Icons.more_horiz_rounded,
        true,
      ),
      _ => (s.tapToSpeak, Icons.mic_none_rounded, false),
    };

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Read over, and corrected if the recogniser got it wrong, before any
        // of it is sent (ADR-069).
        if (phase == _VoicePhase.reviewing) ...[
          TextField(
            controller: _chatInput,
            minLines: 1,
            maxLines: 5,
            autofocus: false,
            textDirection: TextDirection.ltr,
            textInputAction: TextInputAction.newline,
            decoration: InputDecoration(
              hintText: s.yourTurn,
              hintTextDirection: Directionality.of(context),
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          Row(
            children: [
              Expanded(
                child: OutlinedButton.icon(
                  onPressed: _busy ? null : () => unawaited(_recordAgain()),
                  icon: const Icon(Icons.mic_rounded, size: 18),
                  label: Text(s.recordAgain),
                ),
              ),
              const SizedBox(width: AppSpacing.sm),
              Expanded(
                child: FilledButton.icon(
                  onPressed: _busy ? null : () => unawaited(_sendReviewed()),
                  icon: const Icon(Icons.send_rounded, size: 18),
                  label: Text(s.send),
                ),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
        ]
        // What the recogniser has heard so far, so the learner can see they are
        // being picked up while they talk.
        else if (_heard.isNotEmpty)
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
                _VoicePhase.listening => () => unawaited(_stopAndReview()),
                _VoicePhase.reviewing => () => unawaited(_recordAgain()),
                _ => null,
              },
            ),
            const SizedBox(width: AppSpacing.md),
            Expanded(child: Text(label, style: context.text.titleSmall)),
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

  /// The learner has finished talking and is looking at what was heard.
  ///
  /// Nothing is sent until they say so. A recogniser mishears, and a turn that
  /// left the moment the microphone closed gave the learner no way to fix it —
  /// they had to send the mistake and let the tutor answer it (ADR-069).
  reviewing,

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
      alignment: fromAi
          ? AlignmentDirectional.centerStart
          : AlignmentDirectional.centerEnd,
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
            color: fromAi
                ? context.palette.border
                : color.withValues(alpha: 0.3),
          ),
        ),
        // English reads left to right even in the Arabic interface — the
        // question mark of "…in your answer?" belongs at the end.
        child: AutoDirectionText(message.text, style: context.text.bodyMedium),
      ),
    );
  }
}

/// The tutor's line on its way: three dots where the reply will appear.
class _TypingBubble extends StatelessWidget {
  const _TypingBubble();

  @override
  Widget build(BuildContext context) {
    return Align(
      alignment: AlignmentDirectional.centerStart,
      child: Container(
        key: const ValueKey('tutor-typing'),
        margin: const EdgeInsets.only(bottom: AppSpacing.xs),
        padding: const EdgeInsets.symmetric(
          horizontal: AppSpacing.md,
          vertical: AppSpacing.sm,
        ),
        decoration: BoxDecoration(
          color: context.colors.surface,
          borderRadius: const BorderRadius.all(AppRadii.md),
          border: Border.all(color: context.palette.border),
        ),
        child: Text(
          '•••',
          style: context.text.titleMedium?.copyWith(
            color: context.colors.onSurface.withValues(alpha: 0.45),
            letterSpacing: 2,
          ),
        ),
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
    final background = correct
        ? context.palette.successSurface
        : context.palette.dangerSurface;

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
/// The listening clip, with a bar the learner can drag.
///
/// Text-to-speech has no notion of a playhead: it is handed a string and it
/// talks until it runs out. So the clip is spoken one sentence at a time and
/// the sentence boundaries *are* the seek points — dragging picks the sentence
/// containing that point and starts again from there. That is what makes going
/// back to a line you missed, jumping to the end, or switching to the slow
/// voice without losing your place possible at all (ADR-068).
///
/// The bar is drawn against characters rather than sentences so it moves like
/// a media scrubber instead of in visible jumps; only where it *lands* is
/// sentence-granular.
class _ListeningPlayer extends ConsumerStatefulWidget {
  const _ListeningPlayer({required this.text, required this.color, this.onAudio});

  final String text;
  final Color color;

  /// Reports play, replay, pause and completion for the admin area (ADR-125).
  final void Function(String event)? onAudio;

  @override
  ConsumerState<_ListeningPlayer> createState() => _ListeningPlayerState();
}

class _ListeningPlayerState extends ConsumerState<_ListeningPlayer> {
  /// The engine, shared with every other player in the app
  /// (`clip_playback.dart`). This widget owns only the layout.
  ///
  /// Not `final`: a clip regenerated on this screen — the learner changes the
  /// level and the passage is written again — arrives as a new [text] on this
  /// same State. See [didUpdateWidget].
  late ClipPlayback _clip;

  /// Where the learner's finger is while dragging, in characters. The bar
  /// follows the finger; the audio does not move until they let go.
  double? _scrubbing;

  @override
  void initState() {
    super.initState();
    _clip = _newClip();
  }

  // `ref.read` here rather than in a `late final` initialiser: a lazy
  // initialiser runs on first use, and the controller's own teardown is one
  // of the uses — by which time reading a provider throws (ADR-080).
  ClipPlayback _newClip() => ClipPlayback(
    speech: ref.read(speechServiceProvider),
    text: widget.text,
    idPrefix: 'listening',
    prepareEarly: true,
  )..addListener(_repaint);

  @override
  void didUpdateWidget(_ListeningPlayer oldWidget) {
    super.didUpdateWidget(oldWidget);
    // The same trap the sentence player fell into: Flutter keeps this State
    // and swaps the text under it, so a controller built once in `initState`
    // would go on speaking the clip the learner just left behind — here, the
    // passage from the level they abandoned, complete with its own scrubber
    // length and clock (ADR-086).
    if (widget.text != oldWidget.text) {
      _clip.removeListener(_repaint);
      _clip.dispose();
      _scrubbing = null;
      _clip = _newClip();
    }
  }

  // Deliberately no auto-play. The clip used to start by itself the moment the
  // screen appeared (§22), which read as the app talking over the learner:
  // they arrive mid-load, the title they were meant to read goes past, and the
  // first thing they do is hunt for the control that makes it stop. The title
  // is the point of the pause — it names what is coming, exactly as an exam
  // prints it above the audio — and the clip begins when they say so (ADR-080).

  /// Whether the clip has been started at all, and whether it had reached
  /// its end — the difference between a play, a replay and a completion.
  bool _everPlayed = false;
  bool _wasFinished = false;

  void _repaint() {
    if (_clip.finished && !_wasFinished) widget.onAudio?.call(ClientEvents.audioCompleted);
    _wasFinished = _clip.finished;
    if (mounted) setState(() {});
  }

  void _toggle() {
    final report = widget.onAudio;
    if (report != null) {
      if (_clip.isPlaying) {
        report(ClientEvents.audioPaused);
      } else if (!_everPlayed) {
        report(ClientEvents.audioPlayed);
      } else if (_clip.finished || _clip.position == 0) {
        report(ClientEvents.audioReplayed);
      }
    }
    _everPlayed = true;
    unawaited(_clip.toggle());
  }

  @override
  void dispose() {
    _clip.removeListener(_repaint);
    // Silences the voice as it goes: nothing may keep talking once this screen
    // is gone, least of all a transcript that is the answer key.
    _clip.dispose();
    super.dispose();
  }

  double get _position => _scrubbing ?? _clip.position;

  /// The clock at a point on the bar — so dragging shows where the finger is
  /// going rather than where the audio still is.
  String _clockAt(double chars) => formatClipTime(_clip.timeAt(chars));

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);
    // From the controller, which follows the voice itself: when the clip ends
    // on its own the control has to change face without being told.
    final playing = _clip.isPlaying;

    return AppCard(
      color: widget.color.withValues(alpha: 0.06),
      borderColor: widget.color.withValues(alpha: 0.3),
      padding: const EdgeInsets.symmetric(
        horizontal: AppSpacing.md,
        vertical: AppSpacing.lg,
      ),
      child: Column(
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              IconButton(
                iconSize: 30,
                color: widget.color,
                tooltip: s.jumpToStart,
                onPressed: () {
                  // Back to the top is a replay in all but name.
                  if (_everPlayed) widget.onAudio?.call(ClientEvents.audioReplayed);
                  unawaited(_clip.seekTo(0));
                },
                icon: const Icon(Icons.first_page_rounded),
              ),
              const SizedBox(width: AppSpacing.sm),
              Container(
                width: 84,
                height: 84,
                decoration: BoxDecoration(
                  color: widget.color.withValues(alpha: 0.14),
                  shape: BoxShape.circle,
                ),
                child: IconButton(
                  iconSize: 40,
                  color: widget.color,
                  onPressed: _toggle,
                  // Three faces, and each one states what the *next* tap does.
                  //
                  // Pausing used to leave a replay face on the button, which
                  // promised the wrong thing: the learner who stopped to think
                  // about a line was told the only way back was from the top.
                  // A paused clip therefore shows the ordinary play triangle,
                  // because the next tap continues (ADR-080). Only a clip that
                  // has actually reached its end offers to replay it.
                  icon: Icon(
                    playing
                        ? Icons.pause_rounded
                        : _clip.finished
                        ? Icons.replay_rounded
                        : Icons.play_arrow_rounded,
                  ),
                ),
              ),
              const SizedBox(width: AppSpacing.sm),
              IconButton(
                iconSize: 30,
                color: widget.color,
                tooltip: s.jumpToEnd,
                onPressed: () =>
                    unawaited(_clip.seekTo(_clip.totalChars.toDouble())),
                icon: const Icon(Icons.last_page_rounded),
              ),
            ],
          ),
          const SizedBox(height: AppSpacing.sm),
          // The scrubber, word-granular where it lands (ADR-082).
          //
          // The unplayed part is drawn in `trackRest` rather than the theme's
          // faint default: this line is the only thing telling the learner
          // where the clip starts and ends, and at border strength it vanished
          // into the card.
          SliderTheme(
            data: SliderTheme.of(context).copyWith(
              trackHeight: 6,
              activeTrackColor: widget.color,
              inactiveTrackColor: context.palette.trackRest,
              thumbColor: widget.color,
              overlayShape: const RoundSliderOverlayShape(overlayRadius: 16),
            ),
            child: Slider(
              value: _position.clamp(0, _clip.totalChars.toDouble()),
              max: _clip.totalChars.toDouble(),
              onChanged: (value) => setState(() => _scrubbing = value),
              onChangeEnd: (value) {
                setState(() => _scrubbing = null);
                unawaited(_clip.seekTo(value));
              },
            ),
          ),
          // How far in, and how long altogether. Both estimated — see
          // `clipClock` — and both read off the same playhead as the bar, so
          // the two can never disagree.
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AppSpacing.sm),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  _clockAt(_position),
                  style: context.text.labelMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.7),
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
                Text(
                  formatClipTime(_clip.total),
                  style: context.text.labelMedium?.copyWith(
                    color: context.colors.onSurface.withValues(alpha: 0.7),
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: AppSpacing.sm),
          // The word under the control names the **state**, not the next tap.
          // It used to name the action, so a learner who had just pressed
          // pause read "Continue" and took the clip to be running (ADR-082).
          Text(
            playing
                ? s.audioPlaying
                : _clip.finished
                ? s.audioFinished
                : _clip.started
                ? s.audioPaused
                : s.playAudio,
            style: context.text.titleSmall,
          ),
          const SizedBox(height: AppSpacing.md),
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(s.normalSpeed)),
              ButtonSegment(value: true, label: Text(s.slowSpeed)),
            ],
            selected: {_clip.slow},
            // Carries on from the sentence being spoken rather than starting
            // the clip again — the controller's rule, not this screen's.
            onSelectionChanged: (value) => _clip.setSlow(value.first),
          ),
          // A device that cannot speak must not block the whole session: the
          // transcript is revealed early rather than after the test
          // (demo review §51).
          if (_clip.audioFailed) ...[
            const SizedBox(height: AppSpacing.md),
            AppCard(
              color: context.palette.warningSurface,
              borderColor: context.palette.warning.withValues(alpha: 0.35),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(
                        Icons.volume_off_rounded,
                        size: 18,
                        color: context.palette.warning,
                      ),
                      const SizedBox(width: AppSpacing.xs),
                      Expanded(
                        child: Text(
                          s.audioUnavailable,
                          style: context.text.labelMedium,
                        ),
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

/// A word question with the word's two speakers at the end of its own line
/// (ADR-119).
class _QuestionWithSpeakers extends StatelessWidget {
  const _QuestionWithSpeakers({
    required this.question,
    required this.word,
    required this.color,
  });

  final String question;
  final String word;
  final Color color;

  @override
  Widget build(BuildContext context) {
    final direction =
        AutoDirectionText.directionOf(question) ?? Directionality.of(context);
    return SizedBox(
      width: double.infinity,
      child: Directionality(
        textDirection: direction,
        child: Text.rich(
          TextSpan(
            style:
                context.text.titleMedium?.copyWith(fontSize: 18, height: 1.45),
            children: [
              TextSpan(text: '$question '),
              WidgetSpan(
                alignment: PlaceholderAlignment.middle,
                child: WordSpeakerButtons(
                  id: 'pronounce:$word',
                  text: word,
                  size: 22,
                  color: color,
                  dense: true,
                ),
              ),
            ],
          ),
          textAlign: TextAlign.start,
        ),
      ),
    );
  }
}
