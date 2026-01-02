# PlateSolvePlus

**PlateSolvePlus** ist ein Plugin für [**N.I.N.A. (Nighttime Imaging ‘N’ Astronomy)**](https://nighttime-imaging.eu/), das es ermöglicht, **Plate Solving über eine separate Guiding-Kamera und ein Guiding-Teleskop** durchzuführen – unabhängig von der Hauptkamera.

Das Plugin richtet sich an Astrofotografen, die ihr Guiding-Setup gezielt für schnelles, robustes und ressourcenschonendes Plate Solving nutzen möchten.

---

## Motivation

In vielen Setups ist das Guiding Scope:
- deutlich kurzbrennweitiger,
- toleranter gegenüber Seeing,
- schneller einsatzbereit,
- und oft frei, während die Hauptkamera belichtet.

**PlateSolvePlus** nutzt genau diesen Vorteil und erweitert N.I.N.A um die Möglichkeit, Plate Solves **über die Guiding-Kamera** durchzuführen – inklusive einer **Offset-Korrektur**, sodass das Ergebnis exakt auf das Zentrum der Hauptkamera referenziert wird.

---

## Funktionsübersicht

### 🔭 Plate Solving über die Guiding-Kamera
PlateSolvePlus verwendet die in N.I.N.A konfigurierte **Guider-Kamera**, um eigenständig Aufnahmen für das Plate Solving zu erstellen.  
Die Hauptkamera bleibt dabei unberührt.

Typische Einsatzszenarien:
- Plate Solving während laufender Hauptbelichtungen
- Schnelle Positionsbestimmung
- Stabileres Solving bei langen Brennweiten

---

### 📐 Separate Parameter für Guiding-Setup
Alle relevanten Einstellungen können **unabhängig von der Hauptkamera** konfiguriert werden:

- Belichtungszeit
- Gain
- Binning
- Brennweite des Guiding Scopes
- Pixelgröße (automatisch oder manuell)
- Plate-Solver-Parameter (Suchradius, Timeout, Downsampling)

Alle Einstellungen sind **profilabhängig** und integrieren sich vollständig in das N.I.N.A-Profilkonzept.

---

### 🔁 Offset-Korrektur zwischen Guide- und Hauptkamera
Da Guiding- und Hauptteleskop in der Regel **nicht exakt koaxial** ausgerichtet sind, bietet PlateSolvePlus eine integrierte Offset-Funktion:

- Einmalige Kalibrierung zwischen Guide-Solve und Main-Solve
- Speicherung eines festen Offsets in **RA/Dec (Bogensekunden)**
- Automatische Anwendung des Offsets auf jedes Guide-Solve-Ergebnis

Das korrigierte Ergebnis entspricht damit exakt dem Bildzentrum der Hauptkamera.

---

### 🧭 Integration in den Imaging-Workflow
PlateSolvePlus stellt ein **Dockable Panel im Imaging-Tab** bereit und fügt sich nahtlos in den bestehenden Workflow ein.

---

## Features

### ✅ Aktuelle Features
- Plate Solving über die Guiding-Kamera
- Separate Capture-Parameter für Guide-Solves
- Eigene Optik-Parameter für das Guiding Scope
- Nutzung der in N.I.N.A konfigurierten Plate Solver (ASTAP, PlateSolve2, ASPS, …)
- Profilabhängige Speicherung aller Einstellungen
- Dockable Panel im Imaging-Tab
- Offset-Korrektur zwischen Guide- und Hauptkamera (RA/Dec)

---

### 🧭 Offset & Alignment
- Kalibrierbarer Offset zwischen Guide- und Hauptkamera
- Speicherung des Offsets in Bogensekunden
- Automatische Anwendung bei jedem Solve
- Schnelle Neukalibrierung bei Setup-Änderungen

---

### 🚀 Geplante Features
- Mount-Slew & Center auf Basis des Guide-Solves
- Optionaler Mount-Sync
- Advanced-Sequencer-Integration
  - Eigene Sequencer Instructions (z. B. „Plate Solve via Guide Scope“)
- Guiding-Awareness (Erkennung aktiven Guidings, optionales Pausieren)
- Erweiterte Offset-Modelle (rotation-aware, Meridian-Flip-robust)
- Anzeige von Solve-Qualität (RMS, Dauer, Sternanzahl)

---

### 🛠 Langfristige Ideen
- Unterstützung einer dedizierten dritten Kamera
- Automatische Offset-Validierung
- Session- oder Setup-spezifische Offset-Profile

---

## Status
PlateSolvePlus befindet sich aktuell in aktiver Entwicklung.  
Der Fokus liegt auf:
- stabiler Integration in N.I.N.A,
- klarer Trennung von Guide- und Imaging-Workflow,
- und einem wartbaren, erweiterbaren Design.

---

## Lizenz
[dieses Projekt ist unter der Mozilla Public License 2.0 veröffentlicht.
Für Details siehe: [Lizenz](https://github.com/Flashy-GER/PlateSolvePlus/tree/master?tab=MPL-2.0-1-ov-file#)

---

## Mitmachen
Feedback, Feature-Ideen und Pull Requests sind willkommen.
