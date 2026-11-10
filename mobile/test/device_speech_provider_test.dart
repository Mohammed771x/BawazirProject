import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_provider.dart';

/// The phone's voice, as the platform actually behaves (ADR-135): the end of
/// an utterance we stopped is reported late, after its replacement has begun.
/// iOS sends it as a completion, Android as a cancellation.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  const channel = MethodChannel('flutter_tts');
  final calls = <String>[];
  late DeviceSpeechProvider voice;
  late int endings;

  Future<void> platformSays(String method) async {
    await TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .handlePlatformMessage(
      channel.name,
      const StandardMethodCodec().encodeMethodCall(MethodCall(method)),
      (_) {},
    );
  }

  setUp(() async {
    calls.clear();
    endings = 0;
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      calls.add(call.method);
      return call.method == 'getVoices' ? <Object?>[] : 1;
    });
    voice = DeviceSpeechProvider()..onComplete = () => endings++;
    await voice.initialise();
  });

  tearDown(() => TestDefaultBinaryMessengerBinding
      .instance.defaultBinaryMessenger
      .setMockMethodCallHandler(channel, null));

  test('an utterance that ends on its own is reported', () async {
    await voice.speak('The shop opens at nine.');
    await platformSays('speak.onComplete');
    expect(endings, 1);
  });

  for (final late in ['speak.onComplete', 'speak.onCancel']) {
    test('the late $late of a stopped utterance is not its replacement\'s',
        () async {
      await voice.speak('The first sentence.');
      await voice.stop();
      await voice.speak('The second sentence.');

      // The first one's ending, arriving now that the second is speaking.
      await platformSays(late);
      expect(endings, 0,
          reason: 'the second utterance is still talking; reporting it over '
              'is how the app lost track of a voice it then could not stop');

      // And the second one's own.
      await platformSays('speak.onComplete');
      expect(endings, 1);
    });
  }

  test('a cancellation nobody asked for is still an ending', () async {
    await voice.speak('The shop opens at nine.');
    // A phone call, the system taking the audio.
    await platformSays('speak.onCancel');
    expect(endings, 1);
  });

  test('stopping a voice that was never handed anything asks the platform '
      'nothing', () async {
    calls.clear();
    await voice.stop();
    expect(calls, isNot(contains('stop')));
  });

  test('a stop always reaches the engine once something was handed to it',
      () async {
    await voice.speak('The shop opens at nine.');
    // Even after the platform said it finished — that report is exactly the
    // thing that can be wrong.
    await platformSays('speak.onComplete');
    calls.clear();
    await voice.stop();
    expect(calls, contains('stop'));
  });
}
