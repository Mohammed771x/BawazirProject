import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';

/// The phone's own voice, taken over mid-sentence: does the service still
/// know the second utterance is speaking once the first one's cancellation
/// has been reported?
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('a new utterance survives the old one being cancelled',
      (tester) async {
    final speech = SpeechService(provider: DeviceSpeechProvider());
    await speech.initialise();
    const a = 'The first sentence is long enough to be interrupted well '
        'before it ends, because it keeps going for quite a while longer.';
    const b = 'The second sentence takes over, and it also runs for several '
        'seconds so that there is time to look at what the service believes.';

    for (var round = 1; round <= 4; round++) {
      final okA = await speech.speak('a', a);
      final aAlone = <bool>[];
      for (var i = 0; i < 5; i++) {
        await tester.pump(const Duration(milliseconds: 250));
        aAlone.add(speech.isSpeakingId('a'));
      }
      final okB = await speech.speak('b', b);
      debugPrint('· round $round: a started=$okA alone=$aAlone, b started=$okB '
          'available=${speech.isAvailable} voice=${speech.voiceDescription}');
      final samples = <bool>[];
      for (var i = 0; i < 8; i++) {
        await tester.pump(const Duration(milliseconds: 250));
        samples.add(speech.isSpeakingId('b'));
      }
      debugPrint('· round $round: believes b is speaking → $samples');
      expect(samples.take(6), everyElement(isTrue),
          reason: 'b runs for ~7 s; the service lost it within 1.5 s');
      await speech.stop();
      await tester.pump(const Duration(milliseconds: 600));
    }
  });
}
