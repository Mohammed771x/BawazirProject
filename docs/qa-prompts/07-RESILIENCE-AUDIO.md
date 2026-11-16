# 07 — الأعطال والتعافي والصوت والميكروفون

طبّق `00-COMMON.md`. الهدف أن يكمل المستخدم عمله أو يحصل على رفض مفهوم مع حفظ تقدمه. أدخل أعطالًا فقط في بيئة اختبار، وميّز الفشل المحقون عن العيب الحقيقي في التعامل معه.

**اقرأ:** `speech_service.dart` و`cloud_speech_provider.dart` وspeech recognition services، `session_screen.dart` و`listening_player.dart` و`clip_playback.dart` وplacement، `session_controller.dart` وHTTP client وtoken store، notifications/tracker، backend resilience/gates وDocker entrypoint. ابدأ باختبارات resilience/session_persistence/voice/listening_audio_stops/voice_takeover_probe/microphone_after_tour.

- انقطاع DNS/إنترنت، شبكة بطيئة ومتذبذبة، response مفقود بعد commit، 429 و503 وtimeout، استجابة ناقصة؛ عند login/refresh/add/answer/complete/weekly review. احتفظ بمدخل المستخدم عند فشل الإرسال ولا تعرض نجاحًا قبل الحفظ. تحقق من DB بعد إعادة المحاولة.
- خروج من شاشة أثناء load، إغلاق إجباري/قتل process، background/foreground، قفل الشاشة ومكالمة أو audio focus جديد، logout أثناء درس، تغيير الحساب مع طلبات قديمة. الرد المتأخر لا يكتب في شاشة disposed أو حساب جديد ولا يعيد token بعد الخروج.
- استئناف جلسة من جهاز ثانٍ بعد إغلاق الأول: نفس passage/words/phase/answers والحالة الخادمية، دون جلسة مكررة أو توليد جديد غير لازم. اختبر جلسة بقيت unbuilt بعد فشل توليد وحد SessionBuildGrace، والتدريب القديم وحد expiry.
- **ADR-135:** تشغيل clip ثم stop ثم صوت جديد ثم callback انتهاء القديم، مرات متقاربة وبترتيبات مختلفة. stop يصل للمزود حتى لو فقدت الواجهة track الصوت. مغادرة passage/question/result أو relevel أو logout توقف الصوت المقصود. result player يستخدم pause/seek/replay بصورة متسقة؛ لا يتداخل tutor مع clip/word audio.
- الصوت السحابي والجهاز: فشل chunk/cache download، fallback، seek/pause/resume، تبديل rate، نص طويل، أول byte/أول صوت، انتهاء مقابل pause. الإلغاء يلغي playback القادم ويحرر slots/files/listeners. تحقق من cleanup لملفات الصوت وحد cache ومنع نموها غير المحدود.
- الميكروفون: permission أول مرة بعد tour، رفض/رفض دائم/قبول لاحق من Settings، العودة للتطبيق، إعادة التسجيل/إلغاء/مراجعة النص قبل إرساله، اتصال سماعة/فصلها، silence/ضجيج/تسجيل طويل. نتيجة recognizer قديمة لا تنتقل للسؤال الجديد. اختبر جهاز Android حقيقي؛ المحاكي لا يثبت مشكلة الصوت المبلغ عنها.
- التذكيرات: رفض الإذن، timezone/DST وتغيير الساعة، refresh يستبدل الجدول ولا يضاعفه، logout يلغي إشعارات الحساب السابق، app resume لا يولد عاصفة requests/events. فرّق خطة إشعار من وصوله وفتحه.
- محليًا فقط: أوقف Python/DB وأعدهما، واختبر timeout/cancellation وتسرب semaphore/connection slots وthread starvation. تحقق من بقاء API الخفيف متاحًا قدر الإمكان، ومن تعافي container حين تنتهي إحدى العمليتين، دون retry storm.

التسليم: جدول failure injected → expected recovery → observed → evidence، crash/ANR/unhandled logs محجوبة، نسبة اكتمال الرحلات بعد التعافي، زمن التعافي، فحص ثبات البيانات، وتسجيل الفيديو للحالات الصوتية. عدم إعادة إنتاج البلاغ نتيجة صالحة مع وصف الجهاز والخطوات؛ لا تختلق سببًا.
