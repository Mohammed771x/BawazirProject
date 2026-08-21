// Renders the app icon from the mark the learner already knows (ADR-057).
//
// Run with `flutter test tool/generate_app_icon.dart`, which is an odd thing to
// say about a generator — but it is the only way to get Flutter's own renderer
// to draw a widget into a file, and drawing it any other way would mean
// maintaining a second copy of the logo that silently drifts from the first.
//
// The icon is therefore not *like* the mark on the sign-in screen. It is that
// mark, rendered at 1024 and resampled down.
//
// Regenerate whenever `WordOsBrand`'s colours or shape change; the sizes and
// destinations are listed below and nothing else needs touching.

import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/theme/app_tokens.dart';

/// The mark alone, without the wordmark beside it.
///
/// A launcher icon is a square at 40 pixels on a home screen; the word "WordOS"
/// at that size is a grey smudge. The letter has to carry it.
class _IconArtwork extends StatelessWidget {
  const _IconArtwork({
    required this.side,
    required this.padded,
    this.plate = true,
  });

  final double side;

  /// Whether to paint the brand colour behind the rounded square.
  ///
  /// True for launcher icons, where a square with transparent corners looks
  /// broken on a launcher that does not round them itself. False for the
  /// launch screen, which is centred on the storyboard's own white.
  final bool plate;

  /// iOS applies no mask and no padding of its own, so the mark fills the
  /// square. Android's adaptive foreground is cropped to a circle by most
  /// launchers, so it needs room around the letter or the corners are cut off.
  final bool padded;

  @override
  Widget build(BuildContext context) {
    final inset = padded ? side * 0.18 : 0.0;

    return SizedBox(
      width: side,
      height: side,
      child: ColoredBox(
        // Behind the rounded square, so a launcher that does not round the
        // corners itself still gets the brand colour rather than black.
        color: plate ? AppColors.brand : const Color(0x00000000),
        child: Padding(
          padding: EdgeInsets.all(inset),
          child: DecoratedBox(
            decoration: BoxDecoration(
              // The same two colours, the same direction, as `WordOsBrand`.
              gradient: const LinearGradient(
                colors: [AppColors.brand, AppColors.listening],
                begin: Alignment.topLeft,
                end: Alignment.bottomRight,
              ),
              borderRadius: BorderRadius.all(
                // Proportional, not the fixed 16 the widget uses at 46 px:
                // a radius that does not scale reads as a different shape at
                // 1024.
                Radius.circular((side - inset * 2) * 0.22),
              ),
            ),
            child: CustomPaint(
              painter: const _WMark(),
              size: Size.square(side - inset * 2),
            ),
          ),
        ),
      ),
    );
  }
}


/// The W, drawn rather than typed.
///
/// A test binding has no real font — it substitutes a placeholder that draws
/// every glyph as a filled rectangle, which is exactly what the first attempt
/// produced: a white box on a blue square. A path has no such dependency, and
/// it stays crisp at 20 pixels where a hinted glyph would not.
///
/// The proportions match the letter as it appears on the sign-in screen: a
/// heavy geometric W, its outer strokes vertical-leaning, its centre peak
/// stopping short of the top.
class _WMark extends CustomPainter {
  const _WMark();

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width;
    final h = size.height;

    // A stroked polyline rather than a filled outline: one number controls the
    // weight, and the joins stay even wherever the shape is scaled to.
    //
    // Proportioned against the mark on the sign-in screen, where the glyph is
    // set at 52% of the square: about 62% of the width and 44% of the height,
    // a shade larger than pure typography because an icon is read at forty
    // pixels on a home screen and a letter sized for a title vanishes there.
    final stroke = w * 0.115;

    final path = Path()
      ..moveTo(w * 0.190, h * 0.285)
      ..lineTo(w * 0.355, h * 0.735)
      ..lineTo(w * 0.500, h * 0.455)
      ..lineTo(w * 0.645, h * 0.735)
      ..lineTo(w * 0.810, h * 0.285);

    canvas.drawPath(
      path,
      Paint()
        ..color = Colors.white
        ..style = PaintingStyle.stroke
        ..strokeWidth = stroke
        ..strokeJoin = StrokeJoin.round
        ..strokeCap = StrokeCap.round
        ..isAntiAlias = true,
    );
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

Future<Uint8List> _render(WidgetTester tester,
    {required int pixels, required bool padded, bool plate = true}) async {
  final key = GlobalKey();

  // The capture is of the boundary's own layer, which is clipped to the test
  // surface — so the surface has to *be* the icon. Left at its default, every
  // file came out 800x600 whatever size the widget asked for.
  await tester.binding
      .setSurfaceSize(Size(pixels.toDouble(), pixels.toDouble()));

  await tester.pumpWidget(
    Directionality(
      textDirection: TextDirection.ltr,
      child: RepaintBoundary(
        key: key,
        child: _IconArtwork(
            side: pixels.toDouble(), padded: padded, plate: plate),
      ),
    ),
  );
  await tester.pumpAndSettle();

  final boundary =
      key.currentContext!.findRenderObject()! as RenderRepaintBoundary;

  // `runAsync` because encoding a PNG is real asynchronous work on the engine,
  // and the test binding's fake clock never lets it finish — without this the
  // await simply never returns and the run dies on the ten-minute timeout.
  final bytes = await tester.runAsync(() async {
    final image = await boundary.toImage();
    final data = await image.toByteData(format: ui.ImageByteFormat.png);
    image.dispose();
    return data!.buffer.asUint8List();
  });

  await tester.binding.setSurfaceSize(null);

  return bytes!;
}

void main() {
  // The sizes each platform asks for. iOS names them itself in Contents.json,
  // Android by density bucket, and the web manifest by pixel count.
  const ios = <String, int>{
    'Icon-App-1024x1024@1x.png': 1024,
    'Icon-App-20x20@1x.png': 20,
    'Icon-App-20x20@2x.png': 40,
    'Icon-App-20x20@3x.png': 60,
    'Icon-App-29x29@1x.png': 29,
    'Icon-App-29x29@2x.png': 58,
    'Icon-App-29x29@3x.png': 87,
    'Icon-App-40x40@1x.png': 40,
    'Icon-App-40x40@2x.png': 80,
    'Icon-App-40x40@3x.png': 120,
    'Icon-App-60x60@2x.png': 120,
    'Icon-App-60x60@3x.png': 180,
    'Icon-App-76x76@1x.png': 76,
    'Icon-App-76x76@2x.png': 152,
    'Icon-App-83.5x83.5@2x.png': 167,
  };

  const android = <String, int>{
    'mipmap-mdpi': 48,
    'mipmap-hdpi': 72,
    'mipmap-xhdpi': 96,
    'mipmap-xxhdpi': 144,
    'mipmap-xxxhdpi': 192,
  };

  const web = <String, int>{
    'Icon-192.png': 192,
    'Icon-512.png': 512,
    'Icon-maskable-192.png': 192,
    'Icon-maskable-512.png': 512,
  };

  testWidgets('writes every launcher icon from the WordOS mark', (tester) async {
    final root = Directory.current.path;

    for (final entry in ios.entries) {
      final bytes = await _render(tester, pixels: entry.value, padded: false);
      File('$root/ios/Runner/Assets.xcassets/AppIcon.appiconset/${entry.key}')
          .writeAsBytesSync(bytes);
    }

    for (final entry in android.entries) {
      final bytes = await _render(tester, pixels: entry.value, padded: false);
      File('$root/android/app/src/main/res/${entry.key}/ic_launcher.png')
          .writeAsBytesSync(bytes);
    }

    for (final entry in web.entries) {
      // The maskable variants are padded: a maskable icon is cropped by the
      // browser, and an unpadded letter loses its corners.
      final bytes = await _render(
        tester,
        pixels: entry.value,
        padded: entry.key.contains('maskable'),
      );
      File('$root/web/icons/${entry.key}').writeAsBytesSync(bytes);
    }

    // Favicon, which is the same mark at the smallest size anyone sees it.
    File('$root/web/favicon.png')
        .writeAsBytesSync(await _render(tester, pixels: 64, padded: false));

    // The launch screen, so the first thing the app shows is the mark rather
    // than a white flash. Transparent behind the rounded square: the
    // storyboard paints its own white, and the image view centres this on it
    // at its natural size — 168 points, as the storyboard declares.
    const launch = <String, int>{
      'LaunchImage.png': 168,
      'LaunchImage@2x.png': 336,
      'LaunchImage@3x.png': 504,
    };

    for (final entry in launch.entries) {
      final bytes = await _render(
          tester, pixels: entry.value, padded: false, plate: false);
      File('$root/ios/Runner/Assets.xcassets/LaunchImage.imageset/${entry.key}')
          .writeAsBytesSync(bytes);
    }
  });
}
