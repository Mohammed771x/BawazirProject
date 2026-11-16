# 08 — حدود الطلبات وعدالة الاستخدام

طبّق `00-COMMON.md`. افحص أن السياسة تعمل كما ضُبطت، وأن الاستخدام الطبيعي لا يتعطل دون داعٍ. هذا ليس قياس أقصى سعة؛ التقرير 09 يقيس السعة.

**اقرأ:** `Program.cs` و`RateLimitOptions.cs` و`ClientAddress.cs` و`CapacityOptions.cs`، mappings لكل endpoint، وApiFactory والاختبارات الأمنية/السعة؛ headers الفعلية من proxy عند توفر بيئة مصرح بها.

## التجارب

1. استخرج **القيم الفعالة**: مصدر الإعداد والـoverrides وpartition/window لكل policy. الافتراضات المصدرية: Auth 10/15min/IP؛ Lookup 120/min؛ Expensive 30/min؛ Voice 120/min؛ Global 300/min. لا تختبر قيم Auth=1000 في ApiFactory ثم تصف الإنتاج بأنه اختُبر.
2. لكل policy: N−1/N/N+1، reset boundary، bursts قرب نهاية وبداية النافذة، recovery بعد 429، أثر Global مع policy الخاص. استخدم limits أصغر في fixture لعزل السلوك، ثم عينة تحقق محدودة بإعدادات البيئة المستهدفة دون تغيير الإنتاج.
3. مستخدم واحد من عدة IPs، مستخدمان من IP واحد، Anonymous، tokens منتهية، refresh، عشرات الطلاب وراء NAT مدرسة. تحقق من أن login/reset/refresh تشترك في السياسة كما هو مقصود، وأن افتتاح التطبيق بعد انتهاء access token لا يستهلك حصة جميع الطلاب بسبب proxy.
4. `True-Client-IP` وX-Forwarded-For متعدد القيم، header مفقود/غير صالح، IPv4/IPv6. افحص trusted proxy chain وهل overwrites/appends فعلًا. لا تثق بتوثيق قديم، ولا تعتبر تدوير header من load generator محاكاة IPs حقيقية. اختبر spoofing في نسخة محلية مماثلة؛ إثبات تجاوزه محليًا وحده لا يثبت قابلية تجاوزه خلف Render.
5. جلسة طبيعية: lookup مع debounce، audio prefetch وأصوات الأسئلة، STT، شروع في جلسة، writing/speaking، events والتذكيرات ولوحة الإدارة. قِس الطلبات/دقيقة لكل مستخدم وأظهر headroom، لا تفرض سقفًا عشوائيًا جديدًا.
6. استخرج جميع المسارات التي تستدعي AI، بما فيها add/change meaning وcomplete أو أي فرع غير موسوم Expensive. اختبر الحماية الفعلية من global/admission/provider، ثم قيّم إن كان هناك مسار غير محدود حقًا؛ عدم وجود attribute وحده لا يثبت غياب الحماية.
7. رفض 429 وشكل الخطأ وRetry-After إن كان متاحًا، bounded retry/backoff/jitter في العميل، عدم logout أو فقد إجابة عند الرفض. غياب Retry-After ملاحظة حسب متطلبات التعافي، لا مانع نشر تلقائي.
8. saturate admission gate في fixture: 503 SERVER_BUSY/AI_BUSY مفهومة وتحرير slot عند الفشل والإلغاء. فرّق رفض rate policy عن امتلاء الموارد وعن 429 من مزود AI.
9. إن كانت هناك عدة instances، اختبر توزيع الطلبات وتضاعف budgets المحلية وإعادة التشغيل. لا تطالب ببنية موزعة إذا النسخة واحدة والحمل المقاس لا يحتاجها؛ اذكر حدود النتيجة.

التقرير: policy/endpoint/partition/config/accepted/rejected/reset/headroom، trace لسيناريو NAT والتحديث، وأثر المستخدم. اكتب «الحدود تعمل وتسمح بالرحلة المختبرة» إذا أثبتت ذلك. احتفظ بنتائج الرفض المتوقعة منفصلة عن أخطاء الرحلات الصحيحة.
