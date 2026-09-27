import 'package:flutter_test/flutter_test.dart';
import 'package:speech_to_text/speech_recognition_error.dart';
import 'package:speech_to_text/speech_recognition_result.dart';
import 'package:speech_to_text/speech_to_text.dart';
import 'package:wordos/core/audio/speech_recognition_service.dart';

/// What a learner said must survive the recogniser's own session boundaries.
///
/// Reported from students' Android phones: pause for a moment in Speaking and
/// everything said before the pause is gone. Android closes its recognition
/// session on a silence — often with `error_speech_timeout` or
/// `error_no_match` and **no final result** — so the words were never more
/// than a partial, and the reopened session's first result replaced them.
///
/// These tests replay that sequence against a fake recogniser. Each is written
/// as the platform behaves, not as it is documented to.
void main() {
  late _FakeRecogniser platform;
  late SpeechRecognitionService service;

  setUp(() async {
    platform = _FakeRecogniser();
    service = SpeechRecognitionService(speech: platform);
    expect(await service.startListening(), isTrue);
  });

  /// The service waits a beat before reopening a closed session.
  Future<void> letItReopen() =>
      Future<void>.delayed(const Duration(milliseconds: 250));

  test('Android: a pause closes the session without a final result, '
      'and the words survive it', () async {
    platform.partial('my name is');

    // The silence. Android reports the timeout and closes — nothing final.
    platform.error('error_speech_timeout');
    platform.status('notListening');
    await letItReopen();
    expect(platform.sessions, 2, reason: 'the microphone was reopened');

    // The new session starts empty, then the learner goes on.
    platform.partial('');
    platform.partial('Ahmed');

    expect(service.heard, 'my name is Ahmed');
    expect(await service.stopAndRead(), 'my name is Ahmed');
  });

  test('a final result with no words keeps what was being heard', () async {
    platform.partial('I like reading');

    // What Android sends after `error_no_match`: final, and empty.
    platform.finalResult('');

    expect(service.heard, 'I like reading');
  });

  test('an empty partial never erases words already heard', () async {
    platform.partial('hello');
    platform.partial('');

    expect(service.heard, 'hello');
  });

  test('a late result from a closed session is not added twice', () async {
    platform.partial('one two three');
    final closedSession = platform.currentListener!;

    platform.status('done');
    await letItReopen();

    // The old session's final result arrives after the new one has opened.
    closedSession(_result('one two three', isFinal: true));
    platform.partial('four');

    expect(service.heard, 'one two three four');
  });

  test('several pauses in one answer keep every sentence', () async {
    for (final sentence in [
      'I went to the market',
      'I bought some bread',
      'then I went home',
    ]) {
      platform.partial(sentence);
      platform.status('done');
      await letItReopen();
    }

    expect(
      await service.stopAndRead(),
      'I went to the market I bought some bread then I went home',
    );
  });

  test('final results still accumulate as they always did', () async {
    platform.finalResult('first sentence');
    platform.status('done');
    await letItReopen();
    platform.finalResult('second sentence');

    expect(service.heard, 'first sentence second sentence');
  });

  test('a new turn starts empty', () async {
    platform.partial('the last answer');
    await service.stopAndRead();

    expect(await service.startListening(), isTrue);
    expect(service.heard, '');
  });
}

SpeechRecognitionResult _result(String words, {required bool isFinal}) =>
    SpeechRecognitionResult(
      [SpeechRecognitionWords(words, null, 0.9)],
      isFinal ? ResultType.finalResult.value : ResultType.partial.value,
    );

/// Stands in for the platform plugin: records sessions and lets a test push
/// results, errors and status changes in the order a device would.
class _FakeRecogniser extends Fake implements SpeechToText {
  SpeechErrorListener? _onError;
  SpeechStatusListener? _onStatus;
  SpeechResultListener? currentListener;
  int sessions = 0;

  @override
  Future<bool> initialize({
    SpeechErrorListener? onError,
    SpeechStatusListener? onStatus,
    debugLogging = false,
    Duration finalTimeout = SpeechToText.defaultFinalTimeout,
    List<SpeechConfigOption>? options,
  }) async {
    _onError = onError;
    _onStatus = onStatus;
    return true;
  }

  @override
  Future listen({
    SpeechResultListener? onResult,
    Duration? listenFor,
    Duration? pauseFor,
    String? localeId,
    SpeechSoundLevelChange? onSoundLevelChange,
    cancelOnError = false,
    partialResults = true,
    onDevice = false,
    ListenMode listenMode = ListenMode.confirmation,
    sampleRate = 0,
    SpeechListenOptions? listenOptions,
  }) async {
    sessions++;
    currentListener = onResult;
  }

  @override
  Future<void> stop() async {}

  @override
  Future<void> cancel() async {}

  void partial(String words) =>
      currentListener!(_result(words, isFinal: false));

  void finalResult(String words) =>
      currentListener!(_result(words, isFinal: true));

  void error(String message) =>
      _onError?.call(SpeechRecognitionError(message, false));

  void status(String value) => _onStatus?.call(value);
}
