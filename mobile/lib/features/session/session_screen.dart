import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_providers.dart';
import '../../core/api/wordos_api.dart';
import '../../core/audio/speech_service.dart';
import '../../core/audio/tts_service.dart';
import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/widgets/app_widgets.dart';
import 'session_widgets.dart';

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
  bool _hintShown = false;

  /// Captured eagerly in [initState] because `ref` must not be touched during
  /// [dispose] — Riverpod throws once the element is being torn down, which
  /// would turn "learner backs out of a session" into a crash. A `late final`
  /// initialiser is not enough: it would defer the read to first use, which is
  /// exactly the disposed moment we are avoiding.
  late final WordOsApi _api;

  /// Captured for the same reason as [_api]: `ref` must not be touched once
  /// teardown has started, and the conversation loop outlives a frame.
  late final TtsService _tts;
  late final SpeechService _speech;

  @override
  void initState() {
    super.initState();
    _api = ref.read(wordOsApiProvider);
    _tts = ref.read(ttsServiceProvider);
    _speech = ref.read(speechServiceProvider);
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
    _speech.cancel().ignore();
    _tts.stop().ignore();
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

  Future<void> _start() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      // Resuming is the same call: the server returns the open session for this
      // skill if there is one, so a killed app picks up exactly where it was
      // (and does not spend a second AI call on a new passage).
      final session = await _api.startSession(widget.skill);
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
        }
      });
      // The conversation starts speaking on its own. A learner who opened a
      // Speaking session came to talk, not to press play.
      if (widget.skill == SkillType.speaking && _chat.isNotEmpty) {
        final lastFromAi = _chat.last.fromAi ? _chat.last.text : null;
        if (lastFromAi != null) unawaited(_speakThenListen(lastFromAi));
      }
    } on ApiException catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e;
        _loading = false;
      });
    }
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
    } on ApiException catch (e) {
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
      _snack(e.message);
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
    _snack(e.message);
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
    } on ApiException catch (e) {
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
  Future<void> _speakThenListen(String text) async {
    if (!mounted) return;
    setState(() => _voice = _VoicePhase.speaking);

    await _tts.speakToCompletion(text);
    if (!mounted || _speakingFinished) return;

    await _listenAndSend();
  }

  /// Opens the microphone, waits for the learner to stop, and sends the turn.
  Future<void> _listenAndSend() async {
    if (!mounted || _busy || _speakingFinished) return;

    // A device that cannot listen is not an error state — it falls back to
    // typing, which every other part of the session already supports.
    if (!await _speech.initialise()) {
      if (mounted) setState(() => _voice = _VoicePhase.unavailable);
      return;
    }

    setState(() {
      _voice = _VoicePhase.listening;
      _heard = '';
    });

    final said = await _speech.listenOnce();
    if (!mounted) return;

    if (said == null || said.trim().isEmpty) {
      // Nothing usable. The turn is offered again rather than sent — an empty
      // transcript would waste an AI call and confuse the tutor.
      setState(() => _voice = _VoicePhase.idle);
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
        await _tts.speakToCompletion(turn.aiMessage);
        if (mounted) await _complete();
        return;
      }

      // Straight back around the loop.
      if (_voiceMode) {
        await _speakThenListen(turn.aiMessage);
      } else {
        setState(() => _voice = _VoicePhase.idle);
      }
    } on ApiException catch (e) {
      _snack(e.message);
      if (mounted) setState(() => _voice = _VoicePhase.idle);
    } finally {
      if (mounted && _busy) setState(() => _busy = false);
    }
  }

  void _next() {
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
      _hintShown = false;
      _freeText.clear();
      _tiles.clear();
      _usedTileIndexes.clear();
    });
  }

  Future<void> _complete() async {
    setState(() => _busy = true);
    try {
      final result =
          await _api.completeSession(_session!.id);
      if (mounted) setState(() => _result = result);
    } on ApiException catch (e) {
      _snack(e.message);
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
        title: Text(s.skillName(widget.skill)),
        actions: [
          if (_session != null && _result == null)
            Padding(
              padding: const EdgeInsetsDirectional.only(end: AppSpacing.md),
              child: Center(
                child: LevelBadge(label: _session!.levelUsed.label, color: color),
              ),
            ),
        ],
      ),
      body: SafeArea(child: _body(s, color)),
    );
  }

  Widget _body(AppStrings s, Color color) {
    if (_loading) return BusyView(message: s.loading);

    if (_error != null) {
      if (_error!.code == 'NO_WORDS_DUE') {
        return EmptyState(
          icon: Icons.hourglass_bottom_rounded,
          title: s.noWordsDue,
          message: s.noWordsDueBody,
          action: OutlinedButton(
            onPressed: () => Navigator.of(context).maybePop(),
            child: Text(s.backToHub),
          ),
        );
      }
      return ErrorView(
        message: _error!.message,
        retryLabel: s.retry,
        onRetry: _start,
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
    if (!_contentDone) return _contentView(s, color);
    return _questionView(s, color);
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
                    child: HighlightedPassage(content: content, color: color),
                  ),
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
              ref.read(ttsServiceProvider).stop();
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
    if (item == null) return BusyView(message: s.loading);

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
        Text(item.prompt, style: context.text.titleMedium),
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
        Text(item.prompt, style: context.text.titleMedium),
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
                    child: Text(
                      evaluation.suggestion!,
                      style: context.text.bodySmall,
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
        if (item.hint != null && result == null) ...[
          const SizedBox(height: AppSpacing.xs),
          if (_hintShown)
            AppCard(
              color: context.palette.subtleSurface,
              child: Row(
                children: [
                  Icon(Icons.lightbulb_outline_rounded,
                      size: 18, color: context.palette.warning),
                  const SizedBox(width: AppSpacing.xs),
                  Expanded(
                    child: Text(
                      item.hint!,
                      style: context.text.titleSmall
                          ?.copyWith(letterSpacing: 2),
                    ),
                  ),
                ],
              ),
            )
          else
            TextButton.icon(
              onPressed: () => setState(() => _hintShown = true),
              icon: const Icon(Icons.lightbulb_outline_rounded, size: 18),
              label: Text(s.showHint),
            ),
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
              TextButton.icon(
                onPressed: result != null
                    ? null
                    : () => setState(() {
                          _tiles.clear();
                          _usedTileIndexes.clear();
                        }),
                icon: const Icon(Icons.backspace_outlined, size: 18),
                label: Text(s.clear),
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

  final Set<int> _usedTileIndexes = {};

  // ── Speaking ──────────────────────────────────────────────────────────────
  Widget _speakingView(AppStrings s, Color color) {
    final session = _session!;
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
              onPressed: () {
                setState(() => _voiceMode = true);
                unawaited(_listenAndSend());
              },
              icon: const Icon(Icons.mic_rounded, size: 18),
              label: Text(s.useVoice),
            ),
        ],
      );
    }

    final (label, icon, active) = switch (phase) {
      _VoicePhase.speaking => (s.tutorSpeaking, Icons.volume_up_rounded, true),
      _VoicePhase.listening => (s.listeningNow, Icons.mic_rounded, true),
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
              // Idle means something went wrong or the learner interrupted, so
              // the microphone can be reopened by hand.
              onTap: phase == _VoicePhase.idle
                  ? () => unawaited(_listenAndSend())
                  : null,
            ),
            const SizedBox(width: AppSpacing.md),
            Expanded(
              child: Text(label, style: context.text.titleSmall),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.xs),
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: TextButton.icon(
            onPressed: () async {
              await _speech.cancel();
              await _tts.stop();
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
            width: 46,
            height: 52,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: context.colors.surface,
              borderRadius: AppRadii.chipBorder,
              border: Border.all(color: context.palette.border),
            ),
            child: Text(
              letter,
              style: context.text.titleLarge,
            ),
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

  Future<void> _play() async {
    setState(() => _played = true);
    final ok = await ref
        .read(ttsServiceProvider)
        .speak(widget.text, slow: _slow);
    if (mounted && !ok) setState(() => _audioFailed = true);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(stringsProvider);

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
              onPressed: _play,
              icon: Icon(
                _played ? Icons.replay_rounded : Icons.play_arrow_rounded,
              ),
            ),
          ),
          const SizedBox(height: AppSpacing.md),
          Text(
            _played ? s.playAgain : s.playAudio,
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
                  Text(widget.text, style: context.text.bodyMedium),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
