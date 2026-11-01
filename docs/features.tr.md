# Platformlara göre özellikler

[English](features.md) · Türkçe

Rondel'in Windows'ta ve macOS'ta hangi özelliklere sahip olduğu ve her birinin hangi sürümle geldiği. İkisini de [son sürümden](https://github.com/xelfiera-shn/rondel/releases/latest) indirebilirsiniz; kurulum ve kullanım [README](../README.tr.md)'de anlatılıyor.

✅ var, geldiği sürümle · ⬜ henüz yok · — bu platformda gerekmiyor

| | Windows | macOS |
|---|---|---|
| **Çalıştığı sistemler** | Windows 10 ve 11, x64 ve ARM64 | macOS 15 ve sonrası, Apple Silicon ve Intel |
| **İndirilecek dosya** | `Rondel-<sürüm>-win-x64.exe` ya da `-win-arm64.exe` | `Rondel-<sürüm>-osx-arm64.zip` ya da `-osx-x64.zip` |
| **Menüyü açan hareket** | Ctrl + sol fare tuşunu basılı tutmak | ⌥ Option + sol fare tuşunu basılı tutmak |
| **İlk açılış** | Henüz kod imzalı değil: SmartScreen bir kez sorar (Ek bilgi → Yine de çalıştır) | İmzalı, ama Apple'ın noter onayı yok: Sistem Ayarları → Gizlilik ve Güvenlik → Yine de Aç ile bir kez izin verin |

## Menü

| Özellik | Windows | macOS |
|---|---|---|
| Değiştirici tuş ve sol fare tuşu basılı tutulunca menü açılır; normal tıklama ve sürüklemeler eskisi gibi çalışır | ✅ 0.1.0 | ✅ 0.8.0 |
| Her yer için ayrı menü: masaüstü, klasör penceresi, klasör, dosya | ✅ 0.3.0 | ✅ 0.8.0 |
| Dilimlerde oturum ipuçları: son oturumun ne zaman olduğu, kaç oturum bulunduğu | ✅ 0.1.0 | ✅ 0.8.0 |
| Menüde klavye: rakamlar, oklar, Enter, Tab, Esc | ✅ 0.1.0 | ✅ 0.8.0 |
| Uzun proje adları dilimlerine sığar | ✅ 0.3.0 | ✅ 0.8.0 |
| Beş tema, koyu ve açık mod, vurgu renkleri | ✅ 0.5.0 | ✅ 0.8.0 |
| Menüyü fare imlecinin olduğu yerde açan klavye kısayolu | ✅ 0.1.0 | ⬜ |

## Ajanı başlatma

| Özellik | Windows | macOS |
|---|---|---|
| Bir klasörde **Aç**, **Devam Et** ve **Oturum Seç** | ✅ 0.1.0 | ✅ 0.8.0 |
| **Oturum Seç**, en yeni oturumlara açılır | ✅ 0.1.0 | ✅ 0.8.0 |
| Masaüstünde **Son Projeler**: projelere ve onların oturumlarına açılır | ✅ 0.2.0 | ✅ 0.8.0 |
| **Sor**, **Burada Sor** ve **Bu Dosyayı Sor** | ✅ 0.3.0 | ✅ 0.8.0 |
| **Yeni Proje** kartı: ilk prompt, `git init`, plan modu ve şablonlar | ✅ 0.4.0 | ✅ 0.8.0 |
| Terminal seçimi | ✅ 0.1.0: Windows Terminal ya da konsol penceresi | ✅ 0.8.0: Terminal ya da iTerm2 |
| Kabuk seçimi | ✅ 0.1.0: PowerShell 7 ya da Windows PowerShell | — ajan, `PATH` ve alias'larınızla birlikte giriş kabuğunuzda çalışır |

## Aktif ajanlar

| Özellik | Windows | macOS |
|---|---|---|
| Bir ajan bir şey sorduğunda, işini bitirdiğinde ya da hatayla durduğunda bildirim; tıklayınca terminali öne gelir | ✅ 0.6.0 | ⬜ |
| Masaüstü menüsünde her ajan için durum noktasıyla **Aktif Ajanlar** ve onu açan kısayol | ✅ 0.6.0 | ⬜ |

## Uygulama

| Özellik | Windows | macOS |
|---|---|---|
| Bildirim alanında ya da menü çubuğunda bir simge; Rondel yeniden açılınca ayarları gelir | ✅ 0.1.0 | ✅ 0.8.0 |
| Ayarlar penceresi | ✅ 0.1.0 | ✅ 0.8.0 |
| Windows ile başlat / Oturum açılınca başlat | ✅ 0.1.0 | ✅ 0.8.0 |
| Ayarlar'da izinler (Erişilebilirlik, Finder) | — | ✅ 0.8.0 |
| Menüler, aksiyonlar ve ajanlar `settings.json`'da; dosya kaydedilince uygulanır | ✅ 0.1.0 | ✅ 0.8.0 |
| İngilizce ve Türkçe | ✅ 0.1.0 | ✅ 0.8.0 |
| Rondel'in yayın anahtarıyla doğrulanan, uygulama içinden güncelleme | ✅ 0.7.0 | ⬜ yeni zip'i sürüm sayfasından indirin |
| Tanılama: hata ayıklama logu ve `--probe` | ✅ 0.1.0 | ✅ 0.8.0 |

## Planlananlar

Henüz iki platformda da yok; bkz. [yol haritası](roadmap.md).

| Özellik | Windows | macOS |
|---|---|---|
| Başka bir mod için değiştirici tuş, örneğin plan modu için Shift | ⬜ | ⬜ |
| Seçili birden fazla dosya hakkında sorma | ⬜ | ⬜ |
| İlk açılışta hareketi gösteren ipucu | ⬜ | ⬜ |

ARM64 Windows sürümü ve Intel Mac sürümü yayımlanıyor, ancak ikisi de henüz böyle bir bilgisayarda denenmedi. Bunlardan birini kullanıyorsanız nasıl çalıştığını [bildirirseniz](https://github.com/xelfiera-shn/rondel/issues/new/choose) seviniriz.
