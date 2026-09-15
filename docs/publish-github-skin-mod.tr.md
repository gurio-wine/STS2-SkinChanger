# Görününüzü GitHub'da yayınlayın (yazar rehberi)

[简体中文](投稿GitHub皮肤Mod说明.md) · [English](publish-github-skin-mod.md)

«Skin Changer» kurulu oyuncular, deponuzu oyunda **☼Görünüm Atölyesi → Kaynak: GitHub** altında görür ve tek tıkla kurar.
Yapmanız gereken yalnızca üç şey: bir konu (topic) eklemek, bir zip yayınlamak, bir kod yapıştırmak.

(Kendi paketinizi taramak için önce Skin Changer gerekir — Steam Atölyesi'nde arayın.)

## 1. Konuyu ekleyin

Depo sayfanızda: About yanındaki dişli → Topics:

```
sts2-sc-mod
```

Konu eklenmezse ya da yanlış yazılırsa oyun deponuzu asla bulamaz; fork edilen depolar da görünmez.

## 2. Zip ile birlikte bir Release yayınlayın

- Zip, oyunun yüklediği görünüm paketidir (`<id>.json` + `.pck` / `.dll`; yalnızca kart görsellerinden oluşan bir paket de olur).
- Dosyaları zip'in **köküne** ya da klasörlere koyun (kaç kat olursa olsun; depo adını taşıyan klasör yalnızca zip'te birden çok Mod varsa tercih edilir).
- Yalnızca `.zip` okunur: `.rar`, `.7z` ve `.tar.gz` hiç ek yok sayılır ve depo «Tanınmadı» durumunda kalır.
- **En yeni** Release'inize ekleyin ve pre-release olarak **işaretlemeyin** (panel bunları okuyamaz). Tek bir ek 128 MB'ı geçmemelidir.
- Tek zip en kolayıdır. Birden fazlaysa panel depo adını taşıyanı tercih eder, yoksa en büyüğünü seçer.
- Zip, **Release yayınlandıktan sonra da eklenebilir**: «Tara» canlı Release'i yeniden okur; yeni tag veya yeniden gönderim gerekmez.

## 3. Kodu sc.info olarak kaydedin

1. Oyunda → ☼Görünüm Atölyesi → Kaynak **GitHub** → filtre **Tanınmadı** → deponuzu bulun.
2. **Tara**'ya tıklayın (yalnızca bu anda zip'iniz indirilir ve hemen ardından silinir). Pencere bir veya daha fazla **kod** listeler; her birinin yanında bir Kopyala düğmesi vardır.
3. **Depo kökünde** `sc.info` adında bir dosya oluşturun, kodları yapıştırın ve commit edin.
4. Oyuna dönüp Yenile'ye tıklayın: deponuz «Tanınmadı» durumundan kurulabilir bir görünüme dönüşür (kartın adı **depo adı** olur).

### Birden çok kod yapıştırma

**Kod başına bir satır, yukarıdan aşağıya.** Hepsi bu:

```
SCM3 6714 3f2a… (örnek; gerçek kod tek uzun satırdır) 1/2 eJw…Cd34
SCM3 6714 3f2a… (örnek; gerçek kod tek uzun satırdır) 2/2 eJw…Cd34
```

Dört sert kural var:

- **Kodu satırlara bölmeyin.** Bir kod tek satırda bütünlüğünü korumalıdır. Düzenleyicinizin uzun satırı kırarak göstermesi sorun değildir; kodun ortasında Enter'a basmak ölümcüldür.
- **Her kodu yapıştırın.** Pencerede kaç kod göründüyse o kadar. Biri eksik kalırsa tüm paket okunamaz — depo «Tanınmadı» kalır.
- **Kodu düzenlemeyin.** Her kodun içinde bir sağlama toplamı vardır; tek karakteri değiştirmek (boşluk eklemek dahi) onu bozar.
- **Kodu tırnak içine almayın.** `"SCM3 …"` alıntı metni sayılır ve yok sayılır.

Geri kalan her şey esnektir: sıra önemli değildir, boş satırlar önemli değildir, kodların arasına başlık ve açıklama koymak sorun değil, Markdown kod bloğuna almak sorun değil, aynı satırda iki kodu boşlukla ayırmak da çalışır. Dosyayı 64 KB altında tutun.

## Oyuncular ne görür?

Tür/hedef etiketleri ve «yeniden başlatma gerekli» ipucu koddan gelir — tarama sırasında algılanırlar, asla elle doldurmazsınız. Yeni kurulan bir paketi çalışmakta olan oyun yüklemez; bu yüzden panel tıpkı Steam gibi yeniden başlatma ister ve yeniden başlatmadan sonra etkili olur.

## Sık yapılan hatalar

- **Depoyu yeniden adlandırmak veya hesap değiştirmek**: `sahip/depo` çifti değişir, eski kodlar çalışmaz — yeniden tarayıp yenilerini commit edin.
- **Zip olmadan yalnızca dağınık dll / pck dosyaları yayınlamak**: deponuz listelenir ama sonsuza dek «Tanınmadı» kalır, kur düğmesi çıkmaz.
- **Başka bir deponun kodunu yapıştırmak**: o da «Tanınmadı» — kod kendi deposuna bağlıdır.
- **Görünümü güncellemek**: yeni bir Release yayınlayın. Yalnızca **değiştirilen hedefler değiştiğinde** ya da **betik / DLL eklediğinizde (yeniden başlatma gereksinimi değişir)** yeniden tarayıp `sc.info`'yu güncellemeniz gerekir; görsel değiştirmek bunu gerektirmez.

Desteklenen hedefler: karakter, kartlar, canavar, Kadim, tüccar, yoldaş, olay.
