# 03 — عقود API وسلامة البيانات والتزامن

طبّق `00-COMMON.md`. المطلوب قياس سلوك العقود والحفظ تحت الاستخدام العادي والتزامن المنضبط؛ لا تفترض أن كل طلب مكرر خطأ أو أن كل عملية ملزمة بإرجاع نفس body.

**اقرأ:** `backend/src/WordOs.Api/Endpoints/` و`Admin/`، `WordOsDbContext.cs` وmigrations ذات الصلة، `mobile/lib/core/models/` و`http_wordos_api.dart`، `admin-web/src/api/` وschemas في `ai-service/app/main.py`. استخدم `PostgresFixture` الحقيقي؛ لا تستبدل القيود بقاعدة ذاكرة.

- استخرج inventory فعلية لكل method/path/request/response/auth/role/rate-policy. راجع `docs/05-API-CONTRACT.md` مقابل C# وDart وTypeScript وPydantic. غياب OpenAPI أو اختلاف وثيقة ليس وحده كسرًا للمستخدم؛ أثبت contract break إن وجد.
- null/missing/empty، enum غير معروف، GUID خطأ، حدود length وعدد القوائم وحجم body وaudio؛ Unicode/NUL/تشكيل عربي؛ paging سالب/كبير/overflow وتاريخ/مدة غير قابلة للقراءة. المطلوب رفض مضبوط أو normalization موثق، دون 500 غير مقصود أو بيانات جزئية.
- افحص serialization camelCase، الحقول الاختيارية والتوافق مع إصدارات أقدم، `alreadyAdded` الغائب/الجديد، ودعم `offset` مع client جديد وAPI أقدم. لا تعتمد على HTTP success فقط؛ deserialize بالموديل الفعلي.
- نفّذ بمزامنة حاجز 2 ثم 10 طلبات على نفس المورد، وكرر ببذرة موثقة: تسجيل البريد نفسه؛ إضافة sense نفسه؛ بدء نفس المهارة؛ نفس answer؛ complete مرتين؛ complete مع abandon؛ حذف أثناء answer/complete؛ تغيير معنى أثناء جلسة؛ استبدال مع كلمة موجودة؛ weekly start/complete؛ تحديث إعداد من جهازين.
- refresh متزامن من جهازين، رد refresh ضاع ثم إعادة داخل وخارج grace؛ logout/reset مع refresh قيد التنفيذ؛ محاولة reset code متزامنة قرب حد المحاولات. الهدف غياب مكافآت أو جلسات/عائلات tokens غير مقصودة، مع حفظ سلوك الإعادة المسموح. لا تصف grace الموثق تلقائيًا بأنه ثغرة.
- اطلب timeout بعد قبول الخادم للكتابة وقبل وصول الرد، ثم أعد الطلب. قارن الأثر النهائي وعدد الأحداث والمحاولات/exposures. ميّز idempotency الفعلية عن عمل جلب جديد مقبول أو رفض conflict صحيح.
- تحقق من القيود الفريدة الجزئية للكلمات غير المحذوفة والجلسة المفتوحة، FK وcheck constraints وatomicity. افحص فقد الرد، فشل SaveChanges، rollback، وإعادة اتصال DB؛ لا ترجع تطبيقًا يبدو ناجحًا مع نصف عملية.
- تحقق من ترتيب ثابت أثناء pagination والتغير بين الصفحات. لا تشترط snapshot isolation إن لم يعد به المنتج؛ اشرح أثر التكرار/الفقد على الاستخدام الحالي وقابلية التعافي.

التقرير يتضمن جدول contract لكل مجموعة endpoints، نتائج سباقات قبل/بعد مع timestamps/counts، اختبارات regression قابلة للإعادة وSQL assertions ببيانات اختبار. لا تعرض claim عام «thread safe» من تشغيل منفرد. ما لم يتوفر PostgreSQL يسجل BLOCKED بدل PASS.
