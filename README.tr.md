# Rondel

**Yapay zekâ kodlama ajanları için dairesel başlatıcı · Claude Code ile çalışır**

[English](README.md) · Türkçe

> **Durum: erken geliştirme.** Henüz bir sürüm yok. İlk Windows sürümü 0.1.0 olarak yayımlanacak.

Windows masaüstünde veya Dosya Gezgini'nde **Ctrl + sol fare tuşunu** kısa bir süre basılı tutun: imlecin altında dairesel bir menü açılır. Bir dilim seçtiğinizde kodlama ajanınız, doğru klasörde açılmış bir terminalde başlar.

| Nerede basılı tutarsınız | Aksiyonlar | Klasör |
|---|---|---|
| Masaüstünde boş alan | Yeni Proje, Yeni + Prompt | Proje klasörünüz (varsayılan `%USERPROFILE%\Projects`) |
| Dosya Gezgini'nde bir klasörün boş alanı | Yeni Proje, Yeni + Prompt, Burada Aç, Burada Devam Et, Burada Oturum Seç | Açık olan klasör |
| Bir klasörün üzerinde (masaüstü veya Dosya Gezgini) | Aç, Devam Et, Oturum Seç | O klasör |

Normal Ctrl+tık ve Ctrl+sürükle her yerde eskisi gibi çalışır. Rondel yalnızca basılı tuttuğunuzda devreye girer.

## Platformlar ve ajanlar

- **Önce Windows 10 ve 11.** Linux ve macOS, Windows tamamlandıktan sonra planlanıyor (bkz. [yol haritası](docs/roadmap.md)).
- **Ajanlar birer profildir.** İlk desteklenen ajan Claude Code. Her profil, ajanın neler yapabildiğini (açma, devam etme, oturum seçme, prompt ile başlatma) tanımlar; menü yalnızca seçili ajanın desteklediklerini gösterir.

## Gizlilik

Rondel telemetri içermez. Ajanın yerel oturum verisini yalnızca "son oturum 2 sa önce" gibi ipuçları göstermek için okur; kendi ayarları, logları ve (açarsanız) Windows ile başlatma kaydı dışında hiçbir şey yazmaz.

## Katkı

Hata bildirimleri ve fikirler memnuniyetle karşılanır — bkz. [CONTRIBUTING.md](CONTRIBUTING.md). Tasarım notları [docs/](docs/) altındadır.

## Lisans

[MIT](LICENSE) © Ahmet Şahin

---

Rondel bağımsız bir projedir; Anthropic ile bağlantılı değildir, Anthropic tarafından onaylanmamış veya desteklenmemiştir. Claude ve Claude Code, Anthropic, PBC'nin ticari markalarıdır.
