# PlateSolvePlus

**PlateSolvePlus** ist ein Plugin für  
[**N.I.N.A. (Nighttime Imaging ‘N’ Astronomy)**](https://nighttime-imaging.eu/),  
das **Plate Solving, Sync und Centering über eine sekundäre Kamera (Guiding-Kamera) und ein separates Teleskop** ermöglicht.

Der Hauptanwendungsfall ist ein **Fallback-Platesolving**, wenn das Plate Solving über Hauptkamera / Hauptteleskop
(z. B. wegen sehr langer Brennweite oder kleinem Bildfeld) nicht zuverlässig möglich ist.

---

## Motivation

In vielen Setups ist das Guiding-Scope:

- deutlich kurzbrennweitiger  
- toleranter gegenüber Seeing  
- schneller einsatzbereit  
- oft verfügbar, während die Hauptkamera belichtet  

**PlateSolvePlus** nutzt dieses Setup gezielt für Plate Solving und
überträgt das Ergebnis **präzise auf das Hauptteleskop** – inklusive Offset-Modell.

---

## Grundidee

> **Guider sieht genug Himmel → löst → Offset bringt das Hauptteleskop exakt auf Ziel**

Damit wird PlateSolvePlus zu einer robusten Alternative zum Standard-Platesolving in NINA,
ohne den bestehenden Workflow zu ersetzen.

---

## Funktionsübersicht

### 🔭 Plate Solving über die Guiding-Kamera
- Aufnahme und Plate Solving erfolgen **ausschließlich über die sekundäre Kamera**
- Hauptkamera bleibt vollständig unberührt
- Nutzung der in NINA konfigurierten Plate Solver  
  (ASTAP, PlateSolve2, ASPS, …)

Typische Einsatzszenarien:
- Sehr lange Brennweiten
- Kleines FOV der Hauptkamera
- Plate Solving während laufender Hauptbelichtungen

---

### ⚙️ Separate, profilbasierte Einstellungen
Alle relevanten Parameter sind **vom Hauptsetup entkoppelt**:

- Belichtungszeit, Gain, Binning
- Brennweite des Guiding-Scopes
- Pixelgröße (automatisch oder manuell)
- Solver-Parameter  
  (Search Radius, Downsample, Timeout)

Alle Einstellungen sind **profilabhängig** und vollständig in das N.I.N.A-Profil integriert.

---

### 🔁 Offset-Korrektur zwischen Guide- und Hauptteleskop

Da Guide- und Hauptteleskop in der Regel **nicht koaxial** montiert sind,
stellt PlateSolvePlus ein flexibles Offset-Modell bereit:

#### Unterstützte Offset-Modi
- **Rotation (Quaternion)** – empfohlen  
  → rotationsstabil, meridian-flip-robust
- **Arcsec (ΔRA / ΔDec)** – einfacher, klassischer Offset

#### Eigenschaften
- Einmalige Kalibrierung
- Persistente Speicherung im Profil
- Automatische Anwendung auf jedes Solve
- Umschaltbar / deaktivierbar
- Offset jederzeit löschbar

---

### 🎯 Capture + Sync / Capture + Slew (Centering)

PlateSolvePlus bietet zwei zentrale Aktionen:

#### ▶️ Capture + Sync
- Guide-Solve durchführen
- Zielkoordinate auf Hauptteleskop **synchronisieren**
- Fallback: Offset-basierte Korrektur, wenn Sync nicht möglich ist

#### ▶️ Capture + Slew (Centering)
- NINA-ähnliches **iteratives Centering**
- Solve → Fehlerberechnung → Korrektur-Slew
- Abbruch bei Erreichen des Thresholds oder nach Max-Versuchen

**Centering-Logik:**
- Einheit: **arcminutes** (wie in NINA)
- Konfigurierbar:
  - Threshold (arcmin)
  - Max Attempts
- Optionaler Sync-Versuch pro Iteration
- Fallback auf Offset-basierte Korrektur

---

### 🧭 Integration in den Imaging-Workflow
- Eigenes **Dockable Panel im Imaging-Tab**
- Zentrale Konfiguration über `Options`
- Klare Trennung:
  - Standard-NINA-Platesolve
  - PlateSolvePlus-Workflow (Secondary Camera)

---

## Aktueller Feature-Stand

### ✅ Implementiert
- Plate Solving über sekundäre Kamera
- Separate Capture- & Solver-Parameter
- Profilbasierte Settings
- Offset-Modelle:
  - Quaternion (Rotation)
  - Arcsec (ΔRA / ΔDec)
- Capture + Sync
- Capture + Slew (iteratives Centering)
- Sync-Fallback-Logik wie in NINA
- Dockable UI im Imaging-Tab

---

### 🚧 In Arbeit / Geplant
- Sequencer-Integration  
  (eigene Instructions: „Center via Guide Scope“)
- Guiding-Awareness  
  (Erkennung aktiven Guidings, optionales Pausieren)
- Erweiterte Status- & Qualitätsanzeigen
  - Solve-Dauer
  - Separation
  - Iterationen
- Zielkoordinaten-Centering  
  (nicht nur „aktueller Blickpunkt“)

---

## Status
PlateSolvePlus befindet sich in **aktiver Entwicklung**.

Fokus:
- robuste Fallback-Lösung für schwierige Setups
- klare Trennung von Main- und Guide-Workflow
- saubere, NINA-konforme Architektur

---

## Lizenz
Dieses Projekt ist unter der **Mozilla Public License 2.0** veröffentlicht.  
Details siehe: [LICENSE.txt](LICENSE.txt)

---

## Mitmachen
Feedback, Ideen und Pull Requests sind ausdrücklich willkommen.
