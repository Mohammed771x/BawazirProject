import 'package:flutter_test/flutter_test.dart';
import 'package:speech_to_text/speech_recognition_result.dart';
import 'package:speech_to_text/speech_to_text.dart';
import 'package:wordos/core/audio/speech_recognition_service.dart';

/// Dictation that survives a pause and a long answer (ADR-069).
///
/// Every recogniser closes its session on its own: after a silence, and after a
/// minute or two of continuous speech. It then starts the next one with an
/// empty transcript. Keeping only the latest result is what made a five-second
/// pause — or a fourth sentence — wipe everything said before it. The words
/// were never lost by the microphone; they were overwritten by this app.
void main() {
  late _FakeStt stt;
  late SpeechRecognitionService mic;

  setUp(() {
    stt = _FakeStt();
    mic = SpeechRecognitionService(speech: stt);
  });

  test('a pause does not restart the transcript', () async {
    final seen = <String>[];
    await mic.startListening(onPartial: seen.add);

    stt.partial('I went to the shop');
    // The learner stops to think. The platform closes the segment.
    stt.finalResult('I went to the shop');
    stt.status('done');
    await Future<void>.delayed(const Duration(milliseconds: 400));

    // They carry on. This is a brand new recognition session, and its words
    // start from nothing.
    stt.partial('and I bought some bread');

    expect(mic.heard, 'I went to the shop and I bought some bread');
    expect(seen.last, 'I went to the shop and I bought some bread');
  });

  test('a long answer keeps every sentence, not the last few', () async {
    await mic.startListening();

    for (final sentence in [
      'My name is Sara.',
      'I study at the university.',
      'Today I woke up early.',
      'Then I read a book about history.',
      'After that I met my friend.',
    ]) {
      stt.partial(sentence);
      stt.finalResult(sentence);
      stt.status('done');
      await Future<void>.delayed(const Duration(milliseconds: 400));
    }

    final said = await mic.stopAndRead();

    expect(said, contains('My name is Sara.'));
    expect(said, contains('After that I met my friend.'));
    expect(said!.split('.').where((p) => p.trim().isNotEmpty), hasLength(5));
  });

  test('the microphone reopens itself when the platform closes it', () async {
    await mic.startListening();
    expect(stt.listenCalls, 1);

    stt.status('done');
    await Future<void>.delayed(const Duration(milliseconds: 400));

    expect(stt.listenCalls, 2,
        reason: 'a closed platform session is not a finished turn');
    expect(mic.isListening, isTrue);
  });

  test('closing the turn stops it reopening', () async {
    await mic.startListening();
    stt.partial('all done');

    await mic.stopAndRead();
    final calls = stt.listenCalls;

    // Whatever the platform reports on the way down must not restart it.
    stt.status('done');
    await Future<void>.delayed(const Duration(milliseconds: 400));

    expect(stt.listenCalls, calls);
    expect(mic.isListening, isFalse);
  });

  test('a recogniser that hears nothing is given up on, not reopened for ever',
      () async {
    await mic.startListening();

    for (var i = 0; i < 60; i++) {
      stt.status('done');
      await Future<void>.delayed(const Duration(milliseconds: 200));
    }

    expect(mic.isListening, isFalse,
        reason: 'a microphone that is no longer listening must not pretend to');
    expect(stt.listenCalls, lessThan(50));
  });
}

/// The plugin, faked down to the four things this service uses.
class _FakeStt implements SpeechToText {
  SpeechResultListener? _onResult;
  SpeechStatusListener? _onStatus;

  int listenCalls = 0;

  void partial(String words) => _onResult?.call(
      SpeechRecognitionResult.init(
          [SpeechRecognitionWords(words, null, 1)], ResultType.partial));

  void finalResult(String words) => _onResult?.call(
      SpeechRecognitionResult.init(
          [SpeechRecognitionWords(words, null, 1)], ResultType.finalResult));

  void status(String value) => _onStatus?.call(value);

  @override
  Future<bool> initialize({
    SpeechErrorListener? onError,
    SpeechStatusListener? onStatus,
    dynamic debugLogging = false,
    Duration finalTimeout = const Duration(milliseconds: 2000),
    List<SpeechConfigOption>? options,
  }) async {
    _onStatus = onStatus;
    return true;
  }

  @override
  Future<dynamic> listen({
    SpeechResultListener? onResult,
    Duration? listenFor,
    Duration? pauseFor,
    String? localeId,
    SpeechSoundLevelChange? onSoundLevelChange,
    dynamic cancelOnError = false,
    dynamic partialResults = true,
    dynamic onDevice = false,
    ListenMode listenMode = ListenMode.confirmation,
    dynamic sampleRate = 0,
    SpeechListenOptions? listenOptions,
  }) async {
    listenCalls++;
    _onResult = onResult;
  }

  @override
  Future<void> stop() async {}

  @override
  Future<void> cancel() async {}

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName} is not faked');
}
