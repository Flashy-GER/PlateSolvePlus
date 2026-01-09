# PlateSolvePlus

**PlateSolvePlus** ist ein Plugin für  
[**N.I.N.A. (Nighttime Imaging ‘N’ Astronomy)**](https://nighttime-imaging.eu/),  
das Plate Solving und Centering **über eine zweite Kamera (z. B. Guiding-Kamera)** ermöglicht.

👉 Ideal als **Fallback**, wenn Plate Solving über die Hauptkamera nicht zuverlässig funktioniert
(z. B. bei sehr langen Brennweiten oder kleinem Bildfeld).

---

## 📘 Dokumentation

Eine ausführliche Schritt-für-Schritt-Anleitung und eine FAQ findest du in der separaten Dokumentation:

- **[Dokumentationsübersicht](Docu/README.md)**  
- **[Setup & Erste Schritte](Docu/SETUP.md)**  
- **[FAQ & Troubleshooting](Docu/FAQ.md)**

👉 Empfohlener Einstieg für neue Nutzer: **SETUP.md**

---

## 🧭 Wann brauche ich PlateSolvePlus?

PlateSolvePlus ist besonders hilfreich, wenn:

- dein Hauptteleskop **sehr langbrennweitig** ist  
- das Bildfeld der Hauptkamera **zu klein** für zuverlässiges Plate Solving ist  
- Plate Solving mit der Hauptkamera **häufig fehlschlägt**  
- du ein **Guiding-Scope mit großem Gesichtsfeld** nutzt  

💡 **Typisches Szenario**  
> *Das Guiding-Scope sieht genug Sterne → löst zuverlässig → PlateSolvePlus bringt das Hauptteleskop exakt auf dieses Ziel.*

---

## 🧠 Grundprinzip (einfach erklärt)

1. PlateSolvePlus macht ein Bild mit der **Guiding- / Secondary-Kamera**
2. Dieses Bild wird platesolved
3. Ein einmal kalibrierter **Offset** rechnet die Position auf das Hauptteleskop um
4. Das Hauptteleskop wird **gesynct oder geslewed**
5. Optional wird das Ganze wiederholt, bis das Ziel perfekt zentriert ist

➡️ Das Ergebnis: **präzises Centering**, auch wenn die Hauptkamera selbst nicht solvebar ist.

---

## ⚙️ Einstellungen (Options)

Alle Einstellungen sind **profilbasiert** und unabhängig von deinem Hauptsetup.

### 📷 Guider Capture
- Belichtungszeit
- Gain
- Binning

### 🔭 Optik / Bildmaßstab
- Brennweite des Guiding-Scopes
- Pixelgröße (automatisch aus Kamera oder manuell)

### 🧩 Plate Solver
- Search Radius
- Downsample
- Timeout

---

## 🔁 Offset zwischen Guide- und Hauptteleskop

Da Guide- und Hauptteleskop meist **nicht exakt parallel** montiert sind, nutzt PlateSolvePlus einen Offset.

### Offset aktivieren
- Checkbox **„Offset verwenden“**

### Offset-Modi
- **Rotation (Quaternion)** ✅ *empfohlen*  
  - robust gegen Meridian Flip
  - rotationsstabil
- **Arcsec (ΔRA / ΔDec)**  
  - einfacher Offset
  - manuelle Werte

### Offset löschen
- Button **„Delete Offset“** setzt alle Offset-Werte zurück

> 💡 Offset muss **einmal kalibriert** werden und wird danach automatisch angewendet.

---

## 🎯 Bedienung im Imaging-Tab

PlateSolvePlus bringt eine eigene View mit drei Hauptaktionen:

### ▶️ Capture
- Nimmt ein Bild mit der Secondary Camera auf
- Führt ein Plate Solve durch
- Zeigt Koordinaten & Solve-Ergebnis an

### ▶️ Capture + Sync
- Plate Solve über Secondary Camera
- Synchronisiert das Hauptteleskop auf die berechnete Position
- Falls Sync nicht möglich ist: automatische Offset-Korrektur

### ▶️ Capture + Slew (Centering)
- Iteratives Centering wie in NINA
- Solve → Fehler messen → Korrektur-Slew
- Wiederholt sich, bis das Ziel zentriert ist

**Centering-Einstellungen:**
- Threshold in **arcminutes** (wie in NINA)
- Maximale Anzahl Versuche

---

## 🎯 Wann benutze ich was?

| Aktion | Wann sinnvoll |
|------|---------------|
| Capture | Nur prüfen, ob Solving funktioniert |
| Capture + Sync | Grobe Ausrichtung / initiale Kalibrierung |
| Capture + Slew | Präzises Centering vor Belichtungsstart |

---

## ✅ Aktueller Funktionsumfang

- Plate Solving über Guiding-/Secondary-Kamera
- Vollständig getrennte Einstellungen vom Hauptsetup
- Offset-Korrektur:
  - Rotation (Quaternion)
  - Arcsec (ΔRA / ΔDec)
- Capture + Sync
- Capture + Slew (iteratives Centering)
- Profilbasierte Speicherung
- Nahtlose Integration in NINA

---

## 🚧 Geplant
- Integration in den Sequencer
- Bessere Statusanzeigen (Abweichung, Iterationen)
- Zielkoordinaten-Centering aus Framing / Sequencer

---

## 🧪 Status
PlateSolvePlus befindet sich in **aktiver Entwicklung**, ist aber bereits stabil nutzbar.

Feedback aus der Praxis ist ausdrücklich willkommen!

---

## 📜 Lizenz
Mozilla Public License 2.0  
siehe: `LICENSE.txt`