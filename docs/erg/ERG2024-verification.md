# ERG import verification – ERG2024

Source: ERG2024-Eng-Web-a.pdf (SHA-256 180EB92E649D1ADA7D2F39855DA70D5742FF211D6863A8804ED2ABB835C96E30)  
Imported: 2026-10-03 with ErgImport 1.0.0.0  
Checked: 2026-10-03 08:38

## Automatic checks

- PASS  Guide numbers are continuous (62 guides + 2 printed blank, 111–174)
- PASS  General First Aid section present
- PASS  Every guide has POTENTIAL HAZARDS, PUBLIC SAFETY and EMERGENCY RESPONSE in that order
- PASS  Every guide has a title
- PASS  Materials present (2771)
- PASS  Table 1 entries present (537)
- PASS  Every placard has an initial response guide

## Independent cross-check (Poppler pdftotext)

`python tools/ErgImport/crosscheck.py ERG2024-Eng-Web-a.pdf src/FieldToolkit/Content/erg/erg.json` re-reads every
page with a second, unrelated PDF text extractor and confirms that every imported material (name and ID), guide title
and line, guide note, Table 1 name and distance row, and General First Aid line appears on its recorded page.

```
Checked 2771 materials, 62 guides, 537 Table 1 entries.
9 item(s) to review:
  guide 173 note '* Some substances may also be flammable, corrosive and/or oxidizing' not found
  Table 1 1541 'Acetone cyanohydrin, stabilized (when spilled in water)' name not found as one run on page 295 (check: it may wrap around the distance line)
  Table 1 1955 'Organic phosphate mixed with compressed gas' name not found as one run on page 305 (check: it may wrap around the distance line)
  Table 1 3160 'Liquefied gas, toxic, flammable, n.o.s. (Inhalation Hazard Zone A)' name not found as one run on page 312 (check: it may wrap around the distance line)
  Table 1 3160 'Liquefied gas, toxic, flammable, n.o.s. (Inhalation Hazard Zone B)' name not found as one run on page 312 (check: it may wrap around the distance line)
  Table 1 3162 'Liquefied gas, poisonous, n.o.s. (Inhalation Hazard Zone A)' name not found as one run on page 313 (check: it may wrap around the distance line)
  Table 1 3162 'Liquefied gas, poisonous, n.o.s. (Inhalation Hazard Zone B)' name not found as one run on page 313 (check: it may wrap around the distance line)
  Table 1 3507 'Uranium hexafluoride, radioactive material, excepted package, less than 0.1 kg per package, non-fissile or fissile-excepted (when spilled in water)' name not found as one run on page 328 (check: it may wrap around the distance line)
  Table 1 9191 'Chlorine dioxide, hydrate, frozen (when spilled in water)' name not found as one run on page 335 (check: it may wrap around the distance line)
```

Review of the 9 items (all checked against the printed pages, no data errors):

- **Guide 173 note** – printed with a stray space ("S ome substances…"); the importer joins it to "Some".
- **8 Table 1 names** – the name continues *below* the distance line in the printed table (e.g. 9191 "Chlorine
  dioxide, hydrate, frozen" / distances / "(when spilled in water)"), so it is not one run of text in Poppler's
  output. Each was compared with the page layout: names and distances are correct.

Also checked by hand against the printed pages: shared-distance groups (1005, 1595, 1613, 1744 Zones A/B across a page
break, 1955, 3160/3162 Zones A–D), "Refer to Table 3" rows (1005, 1017), the "— —" explosives entries without ID
numbers, guides 121 and 167 (printed "Intentionally left blank"), the two-column Small/Large Spill block in Guide
131, the CO₂ subscripts, and the placard table (pages 8–9) transcribed into `tools/ErgImport/placards.csv`.

## Manual comparison against the printed guidebook

Open the PDF at each page number and tick each entry that matches exactly.

### Materials (ID list)

- [ ] p.34: 1219 – guide 129 – Isopropanol
- [ ] p.35: 1278 – guide 129 – Propyl chloride
- [ ] p.38: 1400 – guide 138 – Barium
- [ ] p.38: 1411 – guide 138 – Lithium aluminum hydride, ethereal
- [ ] p.39: 1487 – guide 140 – Potassium nitrate and sodium nitrite mixture
- [ ] p.40: 1559 – guide 151 – Arsenic pentoxide
- [ ] p.41: 1585 – guide 151 – Copper acetoarsenite
- [ ] p.42: 1651 – guide 153 – Naphthylthiourea
- [ ] p.42: 1658 – guide 151 – Nicotine sulphate, solution
- [ ] p.47: 1923 – guide 135 – Calcium hydrosulfite (highlighted)
- [ ] p.48: 1955 – guide 123 – Compressed gas, poisonous, n.o.s. (Inhalation Hazard Zone A) (highlighted)
- [ ] p.49: 1978 – guide 115 – Propane
- [ ] p.50: 2045 – guide 130 – Isobutyl aldehyde
- [ ] p.52: 2234 – guide 130 – Chlorobenzotrifluorides
- [ ] p.54: 2367 – guide 130 – alpha-Methylvaleraldehyde
- [ ] p.55: 2448 – guide 133 – Sulfur, molten
- [ ] p.58: 2590 – guide 171 – Asbestos, chrysotile
- [ ] p.58: 2604 – guide 132 – Boron trifluoride diethyl etherate
- [ ] p.59: 2691 – guide 137 – Phosphorus pentabromide (highlighted)
- [ ] p.60: 2746 – guide 156 – Phenyl chloroformate
- [ ] p.61: 2786 – guide 153 – Organotin pesticide, solid, toxic
- [ ] p.62: 2794 – guide 154 – Batteries, wet, filled with acid
- [ ] p.62: 2800 – guide 154 – Batteries, wet, non-spillable
- [ ] p.64: 2905 – guide 154 – Phenolates, solid
- [ ] p.73: 3246 – guide 156 – Methanesulphonyl chloride (highlighted)
- [ ] p.81: 3386 – guide 139 – Poisonous by inhalation liquid, water-reactive, n.o.s. (Inhalation Hazard Zone B) (highlighted)
- [ ] p.81: 3391 – guide 135 – Organometallic substance, solid, pyrophoric
- [ ] p.83: 3477 – guide 153 – Fuel cell cartridges packed with equipment, containing corrosive substances
- [ ] p.84: 3492 – guide 131 – Poisonous by inhalation liquid, corrosive, flammable, n.o.s. (Inhalation Hazard Zone A) (highlighted)
- [ ] p.85: 3512 – guide 173 – Adsorbed gas, toxic, n.o.s. (Inhalation Hazard Zone A) (highlighted)

### Table 1 entries

- [ ] p.293: 1162 Dimethyldichlorosilane (when spilled in water) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.1 km (0.1 mi); large: 30 m (100 ft) / 0.4 km (0.2 mi) / 1.2 km (0.8 mi)
- [ ] p.293: 1196 Ethyltrichlorosilane (when spilled in water) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.5 km (0.3 mi); large: 150 m (500 ft) / 1.8 km (1.2 mi) / 3.7 km (2.3 mi)
- [ ] p.300: 1777 Fluorosulphonic acid (when spilled in water) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.1 km (0.1 mi); large: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.3 km (0.2 mi)
- [ ] p.302: 1859 Silicon tetrafluoride – small: 30 m (100 ft) / 0.2 km (0.1 mi) / 0.8 km (0.5 mi); large: 100 m (300 ft) / 0.5 km (0.3 mi) / 1.8 km (1.2 mi)
- [ ] p.306: 2194 Selenium hexafluoride – small: 200 m (600 ft) / 1.1 km (0.7 mi) / 3.4 km (2.1 mi); large: 600 m (2000 ft) / 3.9 km (2.4 mi) / 7.6 km (4.8 mi)
- [ ] p.307: 2204 Carbonyl sulfide – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.3 km (0.2 mi); large: 300 m (1000 ft) / 1.5 km (1.0 mi) / 3.6 km (2.3 mi)
- [ ] p.313: 3162 Liquefied gas, toxic, n.o.s. (Inhalation Hazard Zone B) – small: 30 m (100 ft) / 0.2 km (0.2 mi) / 1.1 km (0.7 mi); large: 300 m (1000 ft) / 1.4 km (0.9 mi) / 3.7 km (2.3 mi)
- [ ] p.319: 3307 Liquefied gas, poisonous, oxidizing, n.o.s. (Inhalation Hazard Zone C) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.6 km (0.4 mi); large: 100 m (300 ft) / 0.6 km (0.4 mi) / 2.2 km (1.4 mi)
- [ ] p.323: 3355 Insecticide gas, toxic, flammable, n.o.s. (Inhalation Hazard Zone C) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.3 km (0.2 mi); large: 150 m (500 ft) / 1.0 km (0.6 mi) / 2.7 km (1.7 mi)
- [ ] p.324: 3382 Toxic by inhalation liquid, n.o.s. (Inhalation Hazard Zone B) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.2 km (0.1 mi); large: 60 m (200 ft) / 0.5 km (0.3 mi) / 0.8 km (0.5 mi)
- [ ] p.326: 3390 Poisonous by inhalation liquid, corrosive, n.o.s. (Inhalation Hazard Zone B) – small: 30 m (100 ft) / 0.2 km (0.1 mi) / 0.2 km (0.1 mi); large: 60 m (200 ft) / 0.5 km (0.3 mi) / 0.6 km (0.4 mi)
- [ ] p.328: 3493 Toxic by inhalation liquid, corrosive, flammable, n.o.s. (Inhalation Hazard Zone B) – small: 30 m (100 ft) / 0.2 km (0.1 mi) / 0.3 km (0.2 mi); large: 60 m (200 ft) / 0.6 km (0.4 mi) / 0.9 km (0.6 mi)
- [ ] p.328: 3493 Poisonous by inhalation liquid, corrosive, flammable, n.o.s. (Inhalation Hazard Zone B) – small: 30 m (100 ft) / 0.2 km (0.1 mi) / 0.3 km (0.2 mi); large: 60 m (200 ft) / 0.6 km (0.4 mi) / 0.9 km (0.6 mi)
- [ ] p.330: 3515 Adsorbed gas, poisonous, oxidizing, n.o.s. (Inhalation Hazard Zone D) – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.1 km (0.1 mi); large: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.1 km (0.1 mi)
- [ ] p.332: 3516 Adsorbed gas, toxic, corrosive, n.o.s. – small: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.1 km (0.1 mi); large: 30 m (100 ft) / 0.1 km (0.1 mi) / 0.1 km (0.1 mi)

### Guides (check every line of each)

- [ ] Guide 116 – Gases - Flammable (Unstable) (pages 164,165)
  - **POTENTIAL HAZARDS**
    - *FIRE OR EXPLOSION*
      - EXTREMELY FLAMMABLE.
      - Will be easily ignited by heat, sparks or flames.
      - Will form explosive mixtures with air. Acetylene (UN1001, UN3374) may react explosively even in the absence of air.
      - Disilane (UN3553) and Silane (UN2203) will ignite spontaneously in air and may re-ignite.
      - Those substances designated with a (P) may polymerize explosively when heated or involved in a fire.
      - Vapors from liquefied gas are initially heavier than air and spread along ground.
      - Vapors may travel to source of ignition and flash back.
      - Cylinders exposed to fire may vent and release flammable gas through pressure relief devices.
      - Containers may explode when heated.
      - Ruptured cylinders may rocket.
    - *HEALTH*
      - Vapors may cause dizziness or asphyxiation without warning, especially when in closed or confined areas.
      - Some may be toxic if inhaled at high concentrations.
      - Contact with gas or liquefied gas may cause burns, severe injury and/or frostbite.
      - Fire may produce irritating and/or toxic gases.
  - **PUBLIC SAFETY**
      - CALL 911. Then call emergency response telephone number on shipping paper. If shipping paper not available or no answer, refer to appropriate telephone number listed on the inside back cover.
      - Keep unauthorized personnel away.
      - Stay upwind, uphill and/or upstream.
      - Many gases are heavier than air and will spread along the ground and collect in low or confined areas (sewers, basements, tanks, etc.).
    - *PROTECTIVE CLOTHING*
      - Wear positive pressure self-contained breathing apparatus (SCBA).
      - Structural firefighters' protective clothing provides thermal protection but only limited chemical protection.
    - *EVACUATION*
      - Immediate precautionary measure
      - Isolate spill or leak area for at least 100 meters (330 feet) in all directions.
      - Large Spill
      - Consider initial downwind evacuation for at least 800 meters (1/2 mile).
      - Fire
      - If tank, rail tank car or highway tank is involved in a fire, ISOLATE for 1600 meters (1 mile) in all directions; also, consider initial evacuation for 1600 meters (1 mile) in all directions.
  - **EMERGENCY RESPONSE**
    - *FIRE*
      - DO NOT EXTINGUISH A LEAKING GAS FIRE UNLESS LEAK CAN BE STOPPED.
      - Small Fire
      - Dry chemical or CO2.
      - Large Fire
      - Water spray or fog.
      - If it can be done safely, move undamaged containers away from the area around the fire.
      - Fire Involving Tanks
      - Fight fire from maximum distance or use unmanned master stream devices or monitor nozzles.
      - Cool containers with flooding quantities of water until well after fire is out.
      - Do not direct water at source of leak or safety devices; icing may occur.
      - Withdraw immediately in case of rising sound from venting safety devices or discoloration of tank.
      - ALWAYS stay away from tanks in direct contact with flames.
      - For massive fire, use unmanned master stream devices or monitor nozzles; if this is impossible, withdraw from area and let fire burn.
    - *SPILL OR LEAK*
      - ELIMINATE all ignition sources (no smoking, flares, sparks or flames) from immediate area.
      - All equipment used when handling the product must be grounded.
      - Stop leak if you can do it without risk.
      - Do not touch or walk through spilled material.
      - Do not direct water at spill or source of leak.
      - Use water spray to reduce vapors or divert vapor cloud drift. Avoid allowing water runoff to contact spilled material.
      - If possible, turn leaking containers so that gas escapes rather than liquid.
      - Prevent entry into waterways, sewers, basements or confined areas.
      - Isolate area until gas has dispersed.
    - *FIRST AID*
      - Refer to the “General First Aid” section.
      - Specific First Aid:
      - In case of contact with liquefied gas, only medical personnel should attempt thawing frosted parts.
      - In case of burns, immediately cool affected skin for as long as possible with cold water. Do not remove clothing if adhering to skin.
- [ ] Guide 132 – Flammable Liquids - Corrosive (pages 196,197)
  - **POTENTIAL HAZARDS**
    - *FIRE OR EXPLOSION*
      - Flammable/combustible material.
      - May be ignited by heat, sparks or flames.
      - Vapors may form explosive mixtures with air.
      - Vapors may travel to source of ignition and flash back.
      - Most vapors are heavier than air. They will spread along the ground and collect in low or confined areas (sewers, basements, tanks, etc.).
      - Vapor explosion hazard indoors, outdoors or in sewers.
      - Those substances designated with a (P) may polymerize explosively when heated or involved in a fire.
      - Runoff to sewer may create fire or explosion hazard.
      - Containers may explode when heated.
      - Many liquids will float on water.
    - *HEALTH*
      - May cause toxic effects if inhaled or ingested.
      - Contact with substance may cause severe burns to skin and eyes.
      - Fire will produce irritating, corrosive and/or toxic gases.
      - Vapors may cause dizziness or asphyxiation, especially when in closed or confined areas.
      - Runoff from fire control or dilution water may cause environmental contamination.
  - **PUBLIC SAFETY**
      - CALL 911. Then call emergency response telephone number on shipping paper. If shipping paper not available or no answer, refer to appropriate telephone number listed on the inside back cover.
      - Keep unauthorized personnel away.
      - Stay upwind, uphill and/or upstream.
      - Ventilate closed spaces before entering, but only if properly trained and equipped.
    - *PROTECTIVE CLOTHING*
      - Wear positive pressure self-contained breathing apparatus (SCBA).
      - Wear chemical protective clothing that is specifically recommended by the manufacturer when there is NO RISK OF FIRE.
      - Structural firefighters' protective clothing provides thermal protection but only limited chemical protection.
    - *EVACUATION*
      - Immediate precautionary measure
      - Isolate spill or leak area for at least 50 meters (150 feet) in all directions.
      - Spill
      - For highlighted materials: see Table 1 - Initial Isolation and Protective Action Distances.
      - For non-highlighted materials: increase the immediate precautionary measure distance, in the downwind direction, as necessary.
      - Fire
      - If tank, rail tank car or highway tank is involved in a fire, ISOLATE for 800 meters (1/2 mile) in all directions; also, consider initial evacuation for 800 meters (1/2 mile) in all directions.
  - **EMERGENCY RESPONSE**
    - *FIRE*
      - Some of these materials may react violently with water.
      - Small Fire
      - Dry chemical, CO2, water spray or alcohol-resistant foam.
      - Large Fire
      - Water spray, fog or alcohol-resistant foam.
      - If it can be done safely, move undamaged containers away from the area around the fire.
      - Dike runoff from fire control for later disposal.
      - Do not get water inside containers.
      - Fire Involving Tanks, Rail Tank Cars or Highway Tanks
      - Fight fire from maximum distance or use unmanned master stream devices or monitor nozzles.
      - Cool containers with flooding quantities of water until well after fire is out.
      - Withdraw immediately in case of rising sound from venting safety devices or discoloration of tank.
      - ALWAYS stay away from tanks in direct contact with flames.
      - For massive fire, use unmanned master stream devices or monitor nozzles; if this is impossible, withdraw from area and let fire burn.
    - *SPILL OR LEAK*
      - ELIMINATE all ignition sources (no smoking, flares, sparks or flames) from immediate area.
      - All equipment used when handling the product must be grounded.
      - Do not touch or walk through spilled material.
      - Stop leak if you can do it without risk.
      - Prevent entry into waterways, sewers, basements or confined areas.
      - A vapor-suppressing foam may be used to reduce vapors.
      - Absorb with earth, sand or other non-combustible material.
      - For hydrazine, absorb with DRY sand or inert absorbent (vermiculite or absorbent pads).
      - Use clean, non-sparking tools to collect absorbed material.
      - Large Spill
      - Dike far ahead of liquid spill for later disposal.
      - Water spray may reduce vapor, but may not prevent ignition in closed spaces.
    - *FIRST AID*
      - Refer to the “General First Aid” section.
      - Specific First Aid:
      - For corrosives, in case of contact, immediately flush skin or eyes with running water for at least 30 minutes. Additional flushing may be required.
      - In case of burns, immediately cool affected skin for as long as possible with cold water. Do not remove clothing if adhering to skin.
- [ ] Guide 138 – Substances - Water-Reactive (Emitting Flammable Gases) (pages 208,209)
  - **POTENTIAL HAZARDS**
    - *FIRE OR EXPLOSION*
      - Produce flammable gases on contact with water.
      - May ignite on contact with water or moist air.
      - Some react vigorously or explosively on contact with water.
      - May be ignited by heat, sparks or flames.
      - May re-ignite after fire is extinguished.
      - Some are transported in highly flammable liquids.
      - Runoff may create fire or explosion hazard.
    - *HEALTH*
      - Inhalation or contact with vapors, substance or decomposition products may cause severe injury or death.
      - May produce corrosive solutions on contact with water.
      - Fire will produce irritating, corrosive and/or toxic gases.
      - Runoff from fire control or dilution water may cause environmental contamination.
  - **PUBLIC SAFETY**
      - CALL 911. Then call emergency response telephone number on shipping paper. If shipping paper not available or no answer, refer to appropriate telephone number listed on the inside back cover.
      - Keep unauthorized personnel away.
      - Stay upwind, uphill and/or upstream.
      - Ventilate closed spaces before entering, but only if properly trained and equipped.
    - *PROTECTIVE CLOTHING*
      - Wear positive pressure self-contained breathing apparatus (SCBA).
      - Wear chemical protective clothing that is specifically recommended by the manufacturer when there is NO RISK OF FIRE.
      - Structural firefighters' protective clothing provides thermal protection but only limited chemical protection.
    - *EVACUATION*
      - Immediate precautionary measure
      - Isolate spill or leak area in all directions for at least 50 meters (150 feet) for liquids and at least 25 meters (75 feet) for solids.
      - Large Spill
      - Consider initial downwind evacuation for at least 300 meters (1000 feet).
      - Fire
      - If tank, rail tank car or highway tank is involved in a fire, ISOLATE for 800 meters (1/2 mile) in all directions; also, consider initial evacuation for 800 meters (1/2 mile) in all directions.
  - **EMERGENCY RESPONSE**
    - *FIRE*
      - DO NOT USE WATER OR FOAM.
      - Small Fire
      - Dry chemical, soda ash, lime or sand.
      - Large Fire
      - DRY sand, dry chemical, soda ash or lime or withdraw from area and let fire burn.
      - If it can be done safely, move undamaged containers away from the area around the fire.
      - Fire Involving Metals or Powders (Aluminum, Lithium, Magnesium, etc.)
      - Use dry chemical, DRY sand, sodium chloride powder, graphite powder or class D extinguishers; in addition, for Lithium you may use Lith-X® powder or copper powder. Also, see GUIDE 170.
      - Fire Involving Tanks, Rail Tank Cars or Highway Tanks
      - Fight fire from maximum distance or use unmanned master stream devices or monitor nozzles.
      - Do not get water inside containers.
      - Cool containers with flooding quantities of water until well after fire is out.
      - Withdraw immediately in case of rising sound from venting safety devices or discoloration of tank.
      - ALWAYS stay away from tanks in direct contact with flames.
    - *SPILL OR LEAK*
      - ELIMINATE all ignition sources (no smoking, flares, sparks or flames) from immediate area.
      - Do not touch or walk through spilled material.
      - Stop leak if you can do it without risk.
      - Use water spray to reduce vapors or divert vapor cloud drift. Avoid allowing water runoff to contact spilled material.
      - DO NOT GET WATER on spilled substance or inside containers.
      - Small Spill
      - Cover with DRY earth, DRY sand or other non-combustible material followed with plastic sheet to minimize spreading or contact with rain.
      - Dike for later disposal; do not apply water unless directed to do so.
      - Powder Spill
      - Cover powder spill with plastic sheet or tarp to minimize spreading and keep powder dry.
      - DO NOT CLEAN-UP OR DISPOSE OF, EXCEPT UNDER SUPERVISION OF A SPECIALIST.
    - *FIRST AID*
      - Refer to the “General First Aid” section.
      - Specific First Aid:
      - In case of contact with substance, wipe from skin immediately; flush skin or eyes with running water for at least 20 minutes.
