# Companions â€“ Jellyfin-Plugin (Jellyfin 12.x)

![Companions](images/logo.png)

VerknÃ¼pft Filme und Serien mit dem, was sonst noch dazugehÃ¶rt:

- **Soundtrack:** Auf der Film- oder Serienseite erscheint eine Zeile â€žSoundtrackâ€œ mit den passenden Alben aus deinen Musikbibliotheken. Auf der Albumseite steht umgekehrt â€žZum Filmâ€œ bzw. â€žZur Serieâ€œ.
- **Buchvorlage:** Eine Zeile mit dem Buch, E-Book oder HÃ¶rbuch aus deinen Buchservern. Ein Klick Ã¶ffnet es dort. UnterstÃ¼tzt werden:
  - **Audiobookshelf** (API-Token, ab Version 2.26)
  - **Kavita** (Auth Key)
  - **Grimmory** (frÃ¼her BookLore; OPDS-Benutzer)
  - **BookOrbit** (OPDS-Benutzer)
  - jeder andere **OPDS-Katalog** mit Suche
- Staffeln und Episoden zeigen Soundtrack und Buchvorlage ihrer Serie.

## Wie der Abgleich funktioniert

### Soundtracks

Alles bleibt lokal in Jellyfin. Ein Album gilt als Soundtrack eines Films, wenn alle drei Bedingungen erfÃ¼llt sind:

1. Sein Name ohne ZusÃ¤tze ist der Titel oder der Originaltitel des Films. ZusÃ¤tze sind z. B. â€ž(Original Motion Picture Soundtrack)â€œ, â€žOSTâ€œ, â€žMusic from â€¦â€œ oder â€žVol. 1â€œ. So passt â€žThe Lord of the Rings: The Fellowship of the Ring (Original Motion Picture Soundtrack)â€œ auch zu â€žDer Herr der Ringe â€“ Die GefÃ¤hrtenâ€œ, weil der Originaltitel stimmt.
2. Es ist als Soundtrack erkennbar, entweder am Namen oder am Genre (Soundtrack, Score, Filmmusik â€¦).
3. Das Erscheinungsjahr passt: hÃ¶chstens ein Jahr vor bis zwei Jahre nach dem Film, bei Serien innerhalb der Laufzeit.

Klaus Schulzes Album â€žDuneâ€œ von 1979 erkennt das Plugin deshalb nicht als Soundtrack zu *Dune* (2021), den Soundtrack von 1984 auch nicht.

### BÃ¼cher

Jellyfin Ã¼bernimmt von TMDB die Vorlage samt Autor in die Mitwirkenden, z. B. â€žFrank Herbert â€“ Novelâ€œ. AuÃŸerdem kommen SchlagwÃ¶rter wie â€žbased on novel or bookâ€œ als Tags. Das Plugin nutzt beides:

- Gefragt werden die Buchserver nur bei Verfilmungen, also bei Titeln mit Vorlagen-Autor oder einem â€žbased on â€¦â€œ-Tag. Das lÃ¤sst sich abschalten.
- **Mit Vorlagen-Autor:** Das Buch muss von diesem Autor sein. â€žJ. R. R. Tolkienâ€œ, â€žJ.R.R. Tolkienâ€œ und â€žTolkien, J. R. R.â€œ gelten als gleich. Der Titel muss passen: gleich, gleich bis auf einen Untertitel (â€žDune: Der WÃ¼stenplanetâ€œ), oder Teil des Filmtitels (â€žDie GefÃ¤hrtenâ€œ in â€žDer Herr der Ringe â€“ Die GefÃ¤hrtenâ€œ). â€žDune Messiahâ€œ gehÃ¶rt deshalb nicht zu *Dune*, und ein â€žDuneâ€œ von einem anderen Autor auch nicht.
- **Ohne Vorlagen-Autor:** Nur ein exakt gleicher Titel zÃ¤hlt.

Gesucht wird mit dem Titel, dem Originaltitel, deren Haupttitel und dem Nachnamen des Autors. Die Ergebnisse speichert das Plugin 12 Stunden zwischen (einstellbar). Fehlgeschlagene Abfragen wiederholt es nach 10 Minuten. Die Cover lÃ¤dt der Jellyfin-Server mit den Zugangsdaten des Buchservers und reicht sie weiter. Der Browser braucht also keinen Zugang zu den Buchservern, nur fÃ¼r die Links.

## Einstellungen

*Dashboard â†’ Plugins â†’ Companions*

| Einstellung | Standard | Bedeutung |
|---|---|---|
| Show the rows in the web client | an | Hauptschalter |
| Link soundtracks â€¦ | an | Soundtrack-Zeile und â€žZum Filmâ€œ auf Albumseiten |
| Link books and audiobooks â€¦ | an | Buch-Zeile |
| Only look up adaptations | an | Buchserver nur bei Verfilmungen fragen |
| Keep lookups for (hours) | 12 | Zwischenspeicher fÃ¼r Buchabfragen |

FÃ¼r jeden Buchserver gibt es folgende Einstellungen:

| Feld | Bedeutung |
|---|---|
| Kind | Audiobookshelf, Kavita, Grimmory, BookOrbit oder anderer OPDS-Katalog |
| Address used by the Jellyfin server | z. B. `http://192.168.1.10:13378` |
| Address for links in the browser | wenn der Browser den Server unter einer anderen Adresse erreicht (Reverse Proxy); leer = gleiche Adresse |
| API token / Auth key | Audiobookshelf: *Einstellungen â†’ API Keys*. Kavita: *Konto â†’ Auth Keys* |
| OPDS user / password | Grimmory und BookOrbit: OPDS in den Einstellungen einschalten und dort einen OPDS-Benutzer anlegen |
| OPDS catalog path, Web page of a book | nur fÃ¼r andere OPDS-Kataloge; `{id}` steht fÃ¼r die Buch-ID |

**Test connection** prÃ¼ft Adresse und Zugang, ohne zu speichern. FÃ¼r Administratoren zeigt `GET /Companions/Explain/{itemId}`, was das Plugin fÃ¼r einen Titel findet und warum.

## Grenzen

- Die Zeilen erscheinen nur in der WeboberflÃ¤che (Browser, Jellyfin Media Player). Native Apps laden keine Plugin-Skripte.
- In den Buchservern selbst erscheint kein Link zurÃ¼ck zu Jellyfin.
- Kavita durchsucht nur Serientitel, nicht Autoren. Liegt ein Buch dort unter einem ganz anderen Titel als der Film, findet das Plugin es in Kavita nicht. In Audiobookshelf, Grimmory und BookOrbit findet es das Buch Ã¼ber den Autor.
- Getestet ist das Plugin gegen Nachbauten der vier Server-Schnittstellen, die ihrem Quellcode folgen (Audiobookshelf 2.37, Kavita 0.9, Grimmory 3.5, BookOrbit 3.7). Mit einer echten Installation ist es noch nicht gelaufen.

## Installation

### Ãœber das Plugin-Repository (mit Updates)

1. *Dashboard â†’ Plugins â†’ Repositorys â†’ +* und diese URL eintragen:
   `https://raw.githubusercontent.com/skriewel/jellyfin-companions/main/manifest.json`
2. Im Katalog **Companions** installieren, Jellyfin neu starten.

### Manuell

1. Ordner `dist\Companions_1.0.3.0` in das Plugin-Verzeichnis von Jellyfin kopieren.
2. Jellyfin neu starten, im Browser Strg+F5.

## Bauen

```powershell
.\build.ps1
```

## Neue Version verÃ¶ffentlichen

1. Version in der `.csproj` und als Standardwert in `build.ps1` erhÃ¶hen, Changelog in `build.ps1` anpassen.
2. `.\build.ps1` ausfÃ¼hren: baut das Paket nach `dist\` und trÃ¤gt die Version mit PrÃ¼fsumme in `manifest.json` ein.
3. Commit und Push, dann das GitHub-Release anlegen (Tag `v<Version>`, das ZIP aus `dist\` anhÃ¤ngen):
   `gh release create v<Version> dist\<Paket>.zip --title "<Name> <Version>"`
