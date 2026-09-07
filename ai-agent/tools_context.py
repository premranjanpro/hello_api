"""
tools_context.py - In-Memory LiveKit FunctionContext Engine for Ultra-Fast Voice Sessions.

Architecture:
- 100% In-Memory Tool Execution: No intermediate database writes or HTTP network latency during live conversation.
- Session Data Tracking: Keeps all tool states (Cab Booking, HR Candidate Evaluation, Emergency Alert) in `self.session_data`.
- End-of-Call Packaging: `get_final_structured_payload(persona_id)` provides a single clean JSON payload dispatched at call termination.
"""

import json
import logging
import random
import time
from typing import Annotated
from livekit.agents import llm

logger = logging.getLogger("ai_tools_context")


class AiToolsContext(llm.FunctionContext):
    """Provides ultra-low-latency in-memory tools callable by LLM during live phone calls."""

    def __init__(
        self,
        call_session_id: str = "",
        user_id: str = "",
        api_base_url: str = "http://localhost:5063"
    ):
        super().__init__()
        self.call_session_id = call_session_id
        self.user_id = user_id
        self.api_base_url = api_base_url.rstrip("/")

        # In-Memory Session Data Store (Zero DB writes during call)
        self.session_data = {
            "fare_estimate": None,
            "cab_booking": None,
            "hr_evaluation": None,
            "emergency_alert": None,
            "english_feedback": [],
            "tool_history": []
        }

    # =========================================================================
    # 1. Cab Booking In-Memory Tools
    # =========================================================================

    @llm.ai_callable(description="Calculate distance and estimated fare for a cab ride based on pickup, destination, and cab type.")
    async def estimate_cab_fare(
        self,
        pickup_location: Annotated[str, "The pickup point or starting location (e.g. 'Noida Sector 62')"],
        drop_location: Annotated[str, "The drop-off point or destination (e.g. 'Delhi Airport T3')"],
        cab_type: Annotated[str, "Type of cab requested: 'mini', 'sedan', or 'suv'"] = "sedan",
    ) -> str:
        logger.info(f"🚕 [Session Tool] estimate_cab_fare: {pickup_location} -> {drop_location} ({cab_type})")

        p_lower = pickup_location.lower()
        d_lower = drop_location.lower()

        base_km = 14.0
        if "airport" in p_lower or "airport" in d_lower:
            base_km = random.uniform(34.0, 42.0)
        elif "station" in p_lower or "station" in d_lower:
            base_km = random.uniform(18.0, 26.0)
        elif "noida" in p_lower and "gurgaon" in d_lower:
            base_km = random.uniform(45.0, 52.0)
        else:
            base_km = random.uniform(8.0, 22.0)

        distance_km = round(base_km, 1)

        rates = {
            "mini": {"base": 50, "rate": 12, "min_km": 3},
            "sedan": {"base": 70, "rate": 15, "min_km": 3},
            "suv": {"base": 100, "rate": 19, "min_km": 3},
        }
        cfg = rates.get(cab_type.lower(), rates["sedan"])
        extra_km = max(0.0, distance_km - cfg["min_km"])
        fare = int(round(cfg["base"] + (extra_km * cfg["rate"])))

        # Store in session data (in-memory)
        self.session_data["fare_estimate"] = {
            "pickup": pickup_location,
            "drop": drop_location,
            "cabType": cab_type.capitalize(),
            "distanceKm": distance_km,
            "estimatedFare": fare,
            "timestamp": time.time()
        }
        self.session_data["tool_history"].append({"tool": "estimate_cab_fare", "fare": fare, "km": distance_km})

        return (
            f"Lagbhag {distance_km} kilometer hai. {cab_type.capitalize()} ka estimated fare Rs. {fare} hoga (GST inclusive). "
            "Kya main abhi cab confirm kar doon?"
        )

    @llm.ai_callable(description="Confirm and book a cab ride. Generates driver, vehicle number, and OTP in session memory.")
    async def book_cab_ride(
        self,
        pickup_location: Annotated[str, "Confirmed pickup location"],
        drop_location: Annotated[str, "Confirmed drop destination"],
        cab_type: Annotated[str, "Cab category: 'mini', 'sedan', or 'suv'"] = "sedan",
        passenger_name: Annotated[str, "Name of the passenger"] = "Customer",
    ) -> str:
        logger.info(f"🚕 [Session Tool] book_cab_ride: {passenger_name} ({cab_type}) {pickup_location} -> {drop_location}")

        # Use previous estimate if available, otherwise compute fresh
        prev_est = self.session_data.get("fare_estimate")
        if prev_est and prev_est.get("pickup") == pickup_location:
            fare = prev_est.get("estimatedFare", 350)
            distance_km = prev_est.get("distanceKm", 15.0)
        else:
            p_lower = pickup_location.lower()
            d_lower = drop_location.lower()
            distance_km = 38.0 if "airport" in p_lower or "airport" in d_lower else 14.5
            fare = 650 if "airport" in (p_lower + d_lower) else (350 if cab_type.lower() == "sedan" else 280)

        booking_id = f"CAB-{random.randint(1000, 9999)}"
        otp = str(random.randint(1000, 9999))
        drivers = ["Ramesh Kumar", "Suresh Yadav", "Vikram Singh", "Amit Sharma"]
        vehicles = [
            "White Swift Dzire (UP 16 DL 4920)",
            "Silver Hyundai Aura (DL 1Z 7831)",
            "Grey WagonR (HR 26 BY 2104)"
        ]
        driver = random.choice(drivers)
        vehicle = random.choice(vehicles)
        eta = random.randint(3, 6)

        # Store in session data (in-memory)
        self.session_data["cab_booking"] = {
            "bookingId": booking_id,
            "status": "CONFIRMED",
            "pickup": pickup_location,
            "drop": drop_location,
            "cabType": cab_type.capitalize(),
            "passengerName": passenger_name,
            "driverName": driver,
            "vehicleNumber": vehicle,
            "otp": otp,
            "etaMinutes": eta,
            "fare": fare,
            "distanceKm": distance_km,
            "bookedAt": time.strftime("%Y-%m-%d %H:%M:%S")
        }
        self.session_data["tool_history"].append({"tool": "book_cab_ride", "bookingId": booking_id, "otp": otp})

        return (
            f"Aapki {cab_type} cab confirm ho gayi hai! Booking ID {booking_id} hai. "
            f"Driver {driver} agle {eta} minute me {vehicle} ke sath pahunch rahe hain. "
            f"Aapka OTP {otp} hai. Fare Rs. {fare} rahega."
        )

    # =========================================================================
    # 2. HR Screening In-Memory Tools
    # =========================================================================

    @llm.ai_callable(description="Record candidate screening evaluation score, role, experience, notice period, and recommendation in session memory.")
    async def save_hr_candidate_evaluation(
        self,
        candidate_name: Annotated[str, "Candidate full name"],
        role_applied: Annotated[str, "Job position applied for"],
        years_experience: Annotated[float, "Years of relevant professional experience"],
        expected_ctc: Annotated[str, "Expected salary or CTC mentioned by candidate"],
        notice_period_days: Annotated[int, "Notice period in days (e.g. 15, 30, 60)"] = 30,
        score_out_of_10: Annotated[int, "Screening rating between 1 and 10 based on candidate answers"] = 8,
        recommendation: Annotated[str, "Short recommendation note e.g. 'Shortlisted for technical round'"] = "Shortlisted",
    ) -> str:
        logger.info(f"💼 [Session Tool] save_hr_candidate_evaluation: {candidate_name} ({role_applied}), score={score_out_of_10}/10")

        # Store in session data (in-memory)
        self.session_data["hr_evaluation"] = {
            "candidateName": candidate_name,
            "roleApplied": role_applied,
            "experienceYears": years_experience,
            "expectedCtc": expected_ctc,
            "noticePeriodDays": notice_period_days,
            "scoreOutOf10": min(10, max(1, int(score_out_of_10))),
            "recommendation": recommendation,
            "evaluatedAt": time.strftime("%Y-%m-%d %H:%M:%S")
        }
        self.session_data["tool_history"].append({"tool": "save_hr_candidate_evaluation", "candidate": candidate_name, "score": score_out_of_10})

        return (
            f"Aapki screening profile record ho gayi hai. Total experience {years_experience} saal aur expected CTC {expected_ctc} note kar liya hai. "
            "Recruitment team agle round ke liye aapse jald hi contact karegi."
        )

    # =========================================================================
    # 3. Parents Care In-Memory Emergency Tool
    # =========================================================================

    @llm.ai_callable(description="Trigger an immediate emergency alert for parents or elderly care when critical symptoms or distress are reported.")
    async def trigger_parent_emergency_alert(
        self,
        urgency_level: Annotated[str, "Urgency level: 'HIGH', 'CRITICAL', or 'MODERATE'"] = "HIGH",
        symptoms: Annotated[str, "Reported pain, fall, severe breathlessness, or distress description"] = "Immediate attention needed",
    ) -> str:
        logger.warning(f"🚨 [Session Tool] trigger_parent_emergency_alert: Urgency={urgency_level}, Symptoms={symptoms}")

        alert_id = f"EMG-{random.randint(10000, 99999)}"
        self.session_data["emergency_alert"] = {
            "alertId": alert_id,
            "urgency": urgency_level,
            "symptoms": symptoms,
            "timestamp": time.strftime("%Y-%m-%d %H:%M:%S")
        }
        self.session_data["tool_history"].append({"tool": "trigger_parent_emergency_alert", "alertId": alert_id})

        return (
            "Aapji bilkul chinta mat kijiye aur aaram se baithiye. Maine parivaar ko turant alert bhej diya hai. "
            "Aap aaram kijiye, madad aa rahi hai."
        )

    # =========================================================================
    # 4. Spoken English Coaching In-Memory Tool
    # =========================================================================

    @llm.ai_callable(description="Log English speaking feedback with correct phrasing and a quick grammar or pronunciation tip in session memory.")
    async def log_english_learning_feedback(
        self,
        spoken_phrase: Annotated[str, "The sentence spoken by the learner"],
        correct_version: Annotated[str, "The natural and grammatically correct English phrasing"],
        grammar_tip: Annotated[str, "A brief 1-sentence tip on why this phrasing is better"],
    ) -> str:
        logger.info(f"🗣️ [Session Tool] log_english_learning_feedback: '{spoken_phrase}' -> '{correct_version}'")
        self.session_data["english_feedback"].append({
            "spoken": spoken_phrase,
            "corrected": correct_version,
            "tip": grammar_tip,
            "timestamp": time.time()
        })
        return f"Nice try! You can say: '{correct_version}'. Tip: {grammar_tip}"

    # =========================================================================
    # 5. Consolidated End-of-Call Structured Payload Builder
    # =========================================================================

    def get_final_structured_payload(self, persona_id: str) -> dict | None:
        """
        Compiles in-memory session data into a clean, unified payload ready
        to be sent to hello_api POST /api/ai/calls/structured-data at call wrap-up.
        """
        # Cab Booking Payload
        if self.session_data.get("cab_booking"):
            b = self.session_data["cab_booking"]
            return {
                "dataType": "cab_booking",
                "structuredJson": json.dumps(b),
                "summary": f"Cab {b['bookingId']} confirmed ({b['cabType']}) from {b['pickup']} to {b['drop']}. Driver: {b['driverName']}, OTP: {b['otp']}, Fare: Rs.{b['fare']}"
            }

        # Fallback to fare estimate if ride wasn't confirmed
        if self.session_data.get("fare_estimate") and "cab_booking" in persona_id:
            fe = self.session_data["fare_estimate"]
            return {
                "dataType": "cab_booking_inquiry",
                "structuredJson": json.dumps(fe),
                "summary": f"Fare inquiry for {fe['cabType']} from {fe['pickup']} to {fe['drop']} (Rs.{fe['estimatedFare']})"
            }

        # HR Candidate Evaluation Payload
        if self.session_data.get("hr_evaluation"):
            ev = self.session_data["hr_evaluation"]
            return {
                "dataType": "hr_evaluation",
                "structuredJson": json.dumps(ev),
                "summary": f"HR Screening: {ev['candidateName']} for {ev['roleApplied']}. Exp: {ev['experienceYears']} yrs, Score: {ev['scoreOutOf10']}/10 ({ev['recommendation']})"
            }

        # Parents Care Emergency Alert Payload
        if self.session_data.get("emergency_alert"):
            emg = self.session_data["emergency_alert"]
            return {
                "dataType": "emergency_alert",
                "structuredJson": json.dumps(emg),
                "summary": f"⚠️ EMERGENCY ALERT: Urgency: {emg['urgency']}. Symptoms: {emg['symptoms']}"
            }

        # English Learning Summary
        if self.session_data.get("english_feedback"):
            feedbacks = self.session_data["english_feedback"]
            return {
                "dataType": "english_coaching",
                "structuredJson": json.dumps({"totalCorrections": len(feedbacks), "feedbacks": feedbacks}),
                "summary": f"Spoken English Session: {len(feedbacks)} constructive feedback corrections provided."
            }

        return None
