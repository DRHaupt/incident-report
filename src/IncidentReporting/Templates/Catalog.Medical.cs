using IncidentReporting.Models;

namespace IncidentReporting.Templates;

public static partial class FormCatalog
{
    private static void AddMedical(List<FormDefinition> list)
    {
        list.Add(Start("MED-300", "Medical Emergency Report", "Medical",
                "Any medical emergency on premises requiring first responder or EMS involvement.")
            .Include(f => CommonSections.PersonInvolved(f, "Patient"))
            .Section("Presentation")
            .Choice("emergency_type", "Nature of Emergency", true,
                "Cardiac / Chest Pain", "Cardiac Arrest", "Difficulty Breathing", "Seizure", "Stroke Symptoms",
                "Diabetic Emergency", "Allergic Reaction / Anaphylaxis", "Unconscious / Fainting", "Overdose / Poisoning",
                "Trauma / Injury", "Heat Illness", "Cold Exposure", "Psychiatric / Behavioral", "Obstetric", "Other")
            .Choice("consciousness", "Level of Consciousness", "Alert", "Responds to Voice", "Responds to Pain", "Unresponsive")
            .Memo("chief_complaint", "Chief Complaint / Symptoms", required: true)
            .Memo("known_conditions", "Known Medical Conditions")
            .Memo("medications", "Current Medications")
            .Memo("allergies", "Known Allergies")
            .Section("Vital Signs")
            .Time("vitals_time", "Time Taken")
            .Text("pulse", "Pulse (bpm)")
            .Text("respirations", "Respirations (per min)")
            .Text("blood_pressure", "Blood Pressure")
            .Text("spo2", "SpO2 (%)")
            .Text("temperature", "Temperature")
            .Text("blood_glucose", "Blood Glucose")
            .Section("Response")
            .Text("first_responder", "First Responder Name")
            .Time("responder_arrival", "Responder Arrival Time")
            .Multi("care_provided", "Care Provided",
                "CPR", "AED Applied", "AED Shock Delivered", "Oxygen", "Epinephrine Auto-Injector", "Naloxone",
                "Bleeding Control", "Splinting", "Recovery Position", "Cooling / Warming", "Oral Glucose", "Monitoring Only")
            .Number("aed_shocks", "Number of AED Shocks")
            .YesNo("ems_called", "Was 911 / EMS Called?")
            .Time("ems_called_time", "Time EMS Called")
            .Time("ems_arrival_time", "Time EMS Arrived")
            .Text("ems_agency", "EMS Agency / Unit Number")
            .Choice("disposition", "Patient Disposition", true,
                "Transported by EMS", "Transported by Private Vehicle", "Refused Transport", "Returned to Work / Activity", "Sent Home", "Deceased")
            .Text("receiving_hospital", "Receiving Hospital")
            .YesNo("refusal_signed", "Was a Refusal of Care Form Signed?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("MED-310", "First Aid Log Entry", "Medical",
                "Minor injury or illness treated on site with first aid only.")
            .Include(f => CommonSections.PersonInvolved(f, "Person Treated"))
            .Section("First Aid")
            .Memo("complaint", "Injury / Complaint", required: true)
            .Multi("body_parts", "Body Part(s)", CommonSections.BodyParts)
            .Multi("first_aid_given", "Treatment Given",
                "Cleaned Wound", "Bandage / Dressing", "Ice Pack", "Heat Pack", "Eye Wash", "Burn Gel", "Splint",
                "Over-the-Counter Medication", "Rest", "Observation", "Other")
            .Text("supplies_used", "Supplies Used")
            .Text("first_aider", "First Aider Name", required: true)
            .YesNo("first_aider_certified", "First Aider Certified?")
            .Choice("outcome", "Outcome", "Returned to Activity", "Sent Home", "Referred to Physician", "Escalated to Emergency")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("MED-320", "Bloodborne Pathogen / Needlestick Exposure", "Medical",
                "Sharps injury or mucous-membrane exposure to blood or other potentially infectious materials.")
            .Include(f => CommonSections.PersonInvolved(f, "Exposed Person"))
            .Section("Exposure")
            .Choice("bbp_type", "Type of Exposure", true,
                "Needlestick (hollow-bore)", "Needlestick (solid)", "Cut from Sharp Object", "Splash to Eyes",
                "Splash to Mouth / Nose", "Contact with Non-Intact Skin", "Human Bite")
            .Text("device", "Device / Sharp Involved (brand, type)")
            .YesNo("safety_device", "Did the Device Have an Engineered Safety Feature?")
            .YesNo("safety_activated", "Was the Safety Feature Activated?")
            .Choice("device_stage", "When Did Exposure Occur?", "Before Use", "During Use", "After Use, Before Disposal", "During Disposal", "Device Left in Inappropriate Place")
            .Text("body_fluid", "Body Fluid Involved")
            .Section("Source & Follow-Up")
            .Choice("source_known", "Source Individual", "Known", "Unknown")
            .Text("source_id", "Source Identifier (no names)")
            .YesNo("source_tested", "Source Tested / Consent Obtained?")
            .YesNo("hep_b_vaccinated", "Exposed Person Hepatitis B Vaccinated?")
            .YesNo("pep_offered", "Post-Exposure Prophylaxis Offered?")
            .Date("followup_date", "Follow-Up Appointment Date")
            .YesNo("sharps_log", "Entered in Sharps Injury Log?")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("MED-330", "Communicable Disease Exposure Report", "Medical",
                "Workplace or facility exposure to a communicable disease (e.g. influenza, TB, COVID-19).")
            .Section("Disease & Exposure")
            .Text("disease", "Disease / Condition", required: true)
            .Choice("case_status", "Case Status", "Suspected", "Probable", "Confirmed")
            .Date("symptom_onset", "Symptom Onset Date")
            .Date("test_date", "Test Date")
            .Text("test_result", "Test Result")
            .Number("exposed_count", "Number of People Potentially Exposed")
            .Memo("exposed_list", "Areas / Groups Exposed")
            .Multi("controls", "Controls Implemented",
                "Isolation", "Quarantine", "Contact Tracing", "Enhanced Cleaning", "Masking", "Area Closure", "Vaccination Offered", "Notification Letter")
            .YesNo("health_dept_notified", "Public Health Department Notified?")
            .Text("health_dept_case", "Health Department Case Number")
            .Include(CommonSections.Footer)
            .Build());

        list.Add(Start("MED-340", "Animal Bite / Scratch Report", "Medical",
                "Bites or scratches from animals, including information needed for rabies follow-up.")
            .Include(f => CommonSections.PersonInvolved(f, "Victim"))
            .Section("Animal")
            .Choice("animal_species", "Species", true, "Dog", "Cat", "Bat", "Raccoon", "Rodent", "Livestock", "Other Wild Animal", "Other")
            .Text("animal_breed", "Breed / Description")
            .Text("animal_owner", "Owner Name & Address")
            .YesNo("animal_vaccinated", "Rabies Vaccination Current?")
            .Choice("animal_status", "Animal Status", "Owner Has Animal", "Quarantined", "Captured", "At Large", "Deceased / Submitted for Testing")
            .Text("animal_control_no", "Animal Control Report Number")
            .Section("Wound")
            .Choice("wound_type", "Wound Type", "Bite - Broken Skin", "Bite - No Broken Skin", "Scratch", "Saliva Contact")
            .Multi("body_parts", "Body Part(s)", CommonSections.BodyParts)
            .YesNo("wound_washed", "Wound Washed Immediately?")
            .YesNo("medical_referral", "Referred for Medical Care?")
            .Include(CommonSections.Footer)
            .Build());
    }
}
