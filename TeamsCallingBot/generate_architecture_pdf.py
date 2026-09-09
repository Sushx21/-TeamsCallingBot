import os
import sys
from reportlab.lib.pagesizes import letter
from reportlab.lib import colors
from reportlab.lib.units import inch
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, Image, KeepTogether, PageBreak, HRFlowable
)
from reportlab.graphics.shapes import Drawing, Rect, String, Line, Group, Polygon
from reportlab.pdfgen import canvas

class NumberedCanvas(canvas.Canvas):
    def __init__(self, *args, **kwargs):
        super(NumberedCanvas, self).__init__(*args, **kwargs)
        self._saved_page_states = []

    def showPage(self):
        self._saved_page_states.append(dict(self.__dict__))
        self._startPage()

    def save(self):
        num_pages = len(self._saved_page_states)
        for state in self._saved_page_states:
            self.__dict__.update(state)
            self.draw_header_footer(num_pages)
            super(NumberedCanvas, self).showPage()
        super(NumberedCanvas, self).save()

    def draw_header_footer(self, page_count):
        self.saveState()
        self.setFont("Helvetica-Bold", 8)
        self.setFillColor(colors.HexColor("#4B5563"))
        
        # Header (pages 2+)
        if self._pageNumber > 1:
            self.drawString(54, 750, "MICROSOFT TEAMS CALLING BOT — SYSTEM ARCHITECTURE SPECIFICATION")
            self.drawRightString(612 - 54, 750, "CONFIDENTIAL & PROPRIETARY")
            self.setStrokeColor(colors.HexColor("#E5E7EB"))
            self.setLineWidth(0.75)
            self.line(54, 744, 612 - 54, 744)

        # Footer (all pages)
        self.setStrokeColor(colors.HexColor("#E5E7EB"))
        self.setLineWidth(0.75)
        self.line(54, 45, 612 - 54, 45)
        
        self.setFont("Helvetica", 8)
        self.drawString(54, 32, "TeamsCallingBot (TSL AI / TDA Bot) • Enterprise Production Architecture")
        page_str = f"Page {self._pageNumber} of {page_count}"
        self.drawRightString(612 - 54, 32, page_str)
        self.restoreState()


def create_high_level_diagram():
    """Generates the High-Level Architecture vector diagram."""
    w, h = 504, 280
    d = Drawing(w, h)
    
    # Outer container background
    d.add(Rect(0, 0, w, h, fillColor=colors.HexColor("#F8FAFC"), strokeColor=colors.HexColor("#E2E8F0"), strokeWidth=1, rx=8, ry=8))
    
    # Section Labels / Zones
    # 1. External & Users Zone
    d.add(Rect(10, 10, 105, 260, fillColor=colors.HexColor("#EFF6FF"), strokeColor=colors.HexColor("#BFDBFE"), strokeWidth=1, rx=6, ry=6))
    d.add(String(62, 252, "EXTERNAL & USERS", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#1E40AF")))
    
    # User / Teams boxes
    d.add(Rect(18, 175, 90, 60, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#3B82F6"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(63, 218, "Teams Client", fontSize=8, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#1E3A8A")))
    d.add(String(63, 206, "Desktop / Web / Mobile", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(63, 192, "#joincall / !join", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(63, 182, "Zero-Link Chat Trigger", fontSize=6, fontName="Helvetica-Oblique", textAnchor="middle", fillColor=colors.HexColor("#6B7280")))

    d.add(Rect(18, 95, 90, 65, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#0284C7"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(63, 142, "Outlook / Exchange", fontSize=8, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#0369A1")))
    d.add(String(63, 130, "Calendar Forward", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#0284C7")))
    d.add(String(63, 118, "to tsl.ai@... Mailbox", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(63, 105, "Power Automate Flow", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#0D9488")))

    d.add(Rect(18, 22, 90, 60, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#475569"), strokeWidth=1, rx=4, ry=4))
    d.add(String(63, 62, "Admin / REST API", fontSize=8, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#334155")))
    d.add(String(63, 50, "POST /joinCall", fontSize=7, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(63, 38, "POST /api/management", fontSize=7, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))

    # 2. Ingress & Controller Gateway Zone
    d.add(Rect(125, 10, 115, 260, fillColor=colors.HexColor("#F5F3FF"), strokeColor=colors.HexColor("#DDD6FE"), strokeWidth=1, rx=6, ry=6))
    d.add(String(182, 252, "INGRESS GATEWAY", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#5B21B6")))
    
    d.add(Rect(132, 175, 100, 60, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#8B5CF6"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(182, 220, "BotMessagingController", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#5B21B6")))
    d.add(String(182, 208, "POST /api/messages", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#6D28D9")))
    d.add(String(182, 195, "Auto-Extract ThreadId", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))
    d.add(String(182, 183, "Extract Tenant & AadOID", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))

    d.add(Rect(132, 95, 100, 65, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#7C3AED"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(182, 145, "PowerAutomateController", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#5B21B6")))
    d.add(String(182, 133, "/api/powerautomate/*", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#6D28D9")))
    d.add(String(182, 120, "EmailInviteParser", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#D97706")))
    d.add(String(182, 108, "Regex URL & Passcodes", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))

    d.add(Rect(132, 22, 100, 60, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#6D28D9"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(182, 65, "PlatformCallController", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#5B21B6")))
    d.add(String(182, 53, "POST /api/calling/notify", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#6D28D9")))
    d.add(String(182, 40, "Graph SDK Callbacks", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(182, 30, "Signaling & State (202)", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # 3. Bot Core & Orchestration Engine
    d.add(Rect(250, 10, 115, 260, fillColor=colors.HexColor("#ECFDF5"), strokeColor=colors.HexColor("#A7F3D0"), strokeWidth=1, rx=6, ry=6))
    d.add(String(307, 252, "CORE BOT ENGINE", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#065F46")))

    d.add(Rect(257, 180, 100, 55, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#10B981"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(307, 220, "MeetingRegistryService", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#065F46")))
    d.add(String(307, 208, "Disk Persistence (JSON)", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#047857")))
    d.add(String(307, 196, "15s Background Poller", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(307, 186, "Auto-Join Time Window", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))

    d.add(Rect(257, 105, 100, 60, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#059669"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(307, 150, "Bot & Call Handlers", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#065F46")))
    d.add(String(307, 138, "JoinCallByCoordinates", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))
    d.add(String(307, 126, "Organizer TenantId Fix", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#D97706")))
    d.add(String(307, 114, "Multi-Call State Mgmt", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))

    d.add(Rect(257, 22, 100, 70, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#047857"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(307, 75, "App-Hosted Media Platform", fontSize=7.2, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#065F46")))
    d.add(String(307, 63, "C++ MediaPerf / MPAzHost", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(307, 51, "SRTP & UDP 8413 Media", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(307, 39, "Audio, Video & VBSS Sockets", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(307, 29, "net.tcp Local IPC (10101)", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#6B7280")))

    # 4. Media Processing & Outputs Zone
    d.add(Rect(375, 10, 119, 260, fillColor=colors.HexColor("#FFF7ED"), strokeColor=colors.HexColor("#FED7AA"), strokeWidth=1, rx=6, ry=6))
    d.add(String(434, 252, "MEDIA & AI PROCESSING", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#9A3412")))

    d.add(Rect(382, 195, 105, 45, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#F97316"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(434, 228, "SafeVideoMediaBuffer", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#C2410C")))
    d.add(String(434, 216, "15 FPS 720p Mascot Stream", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#EA580C")))
    d.add(String(434, 203, "Stage Gallery Promotion", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))

    d.add(Rect(382, 138, 105, 48, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#EA580C"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(434, 172, "Audio Aggregator & STT", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#C2410C")))
    d.add(String(434, 160, "16kHz PCM Mono Capture", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(434, 149, "Live Whisper Transcriber", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))

    d.add(Rect(382, 80, 105, 48, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#D97706"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(434, 114, "1080p Screen Share Rec", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#C2410C")))
    d.add(String(434, 102, "MjpegAviWriter / VBSS", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(434, 90, "HD Video File Export", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    d.add(Rect(382, 22, 105, 48, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#CA8A04"), strokeWidth=1.5, rx=4, ry=4))
    d.add(String(434, 56, "DocxWriter & MoM", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#C2410C")))
    d.add(String(434, 44, "Automated Word Summary", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(434, 32, "Cloud / GCS Upload", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#7C3AED")))

    # Connective Arrows / Lines
    # Chat to BotMessaging
    d.add(Line(108, 205, 132, 205, strokeColor=colors.HexColor("#3B82F6"), strokeWidth=1.5))
    # Email to PowerAutomate
    d.add(Line(108, 127, 132, 127, strokeColor=colors.HexColor("#0284C7"), strokeWidth=1.5))
    # BotMessaging to Bot
    d.add(Line(232, 205, 257, 145, strokeColor=colors.HexColor("#8B5CF6"), strokeWidth=1.5))
    # PowerAutomate to Registry
    d.add(Line(232, 127, 257, 195, strokeColor=colors.HexColor("#7C3AED"), strokeWidth=1.5))
    # Registry to Bot
    d.add(Line(307, 180, 307, 165, strokeColor=colors.HexColor("#10B981"), strokeWidth=1.5))
    # Bot to MediaPlatform
    d.add(Line(307, 105, 307, 92, strokeColor=colors.HexColor("#059669"), strokeWidth=1.5))
    # MediaPlatform to Sockets
    d.add(Line(357, 65, 382, 215, strokeColor=colors.HexColor("#F97316"), strokeWidth=1.2))
    d.add(Line(357, 55, 382, 160, strokeColor=colors.HexColor("#EA580C"), strokeWidth=1.2))
    d.add(Line(357, 45, 382, 105, strokeColor=colors.HexColor("#D97706"), strokeWidth=1.2))
    d.add(Line(357, 35, 382, 45, strokeColor=colors.HexColor("#CA8A04"), strokeWidth=1.2))

    return d


def create_low_level_diagram():
    """Generates the Low-Level Data Flow & State Machine vector diagram."""
    w, h = 504, 300
    d = Drawing(w, h)
    
    # Outer container background
    d.add(Rect(0, 0, w, h, fillColor=colors.HexColor("#F8FAFC"), strokeColor=colors.HexColor("#E2E8F0"), strokeWidth=1, rx=8, ry=8))
    
    # Column 1: Chat/Email Ingestion & Parsing
    d.add(Rect(12, 185, 145, 100, fillColor=colors.HexColor("#EFF6FF"), strokeColor=colors.HexColor("#3B82F6"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(84, 272, "1. INGESTION & COORDINATES", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#1E3A8A")))
    d.add(String(84, 258, "Incoming Activity / Invite Payload", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(84, 244, "• ConvId: 19:meeting_...thread.v2", fontSize=6, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(84, 233, "• TenantId: f35425af-4755-4e0c...", fontSize=6, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(84, 222, "• Caller OID: from.aadObjectId", fontSize=6, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(84, 211, "• Command: #joincall / #video", fontSize=6, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#D97706")))
    d.add(String(84, 198, "Output: JoinCoordinates Tuple", fontSize=6.5, fontName="Helvetica-Oblique", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Column 2: Graph SDK Call Establishment
    d.add(Rect(180, 185, 145, 100, fillColor=colors.HexColor("#F5F3FF"), strokeColor=colors.HexColor("#8B5CF6"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(252, 272, "2. GRAPH CALL ESTABLISHMENT", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#5B21B6")))
    d.add(String(252, 258, "GraphServiceClient.Calls.AddAsync", fontSize=6.5, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#6D28D9")))
    d.add(String(252, 244, "• MediaAppHost: Custom Hosted", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(252, 233, "• Organizer.User.SetTenantId()", fontSize=6.5, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))
    d.add(String(252, 222, "• MediaConfig: SafeVideo + Audio", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(252, 211, "• 201 Created -> 202 Accepted", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(252, 198, "Call State -> 'Established'", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Column 3: Native App-Hosted Media Engine
    d.add(Rect(347, 185, 145, 100, fillColor=colors.HexColor("#ECFDF5"), strokeColor=colors.HexColor("#10B981"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(419, 272, "3. MEDIA ENGINE INITIALIZATION", fontSize=7.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#065F46")))
    d.add(String(419, 258, "MediaPlatform.CreateMediaSession()", fontSize=6.5, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#047857")))
    d.add(String(419, 244, "• AudioSocket (16kHz 16-bit Mono)", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(419, 233, "• VideoSocket (1280x720 15fps)", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(419, 222, "• VbssSocket (1920x1080 Screen)", fontSize=6.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(419, 211, "• UDP 8413 SRTP Handshake", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(419, 198, "Active Media Session Bound", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Connecting Arrows across row 1
    d.add(Line(157, 235, 180, 235, strokeColor=colors.HexColor("#3B82F6"), strokeWidth=1.5))
    d.add(Line(325, 235, 347, 235, strokeColor=colors.HexColor("#8B5CF6"), strokeWidth=1.5))

    # Row 2: Media Processing Pipelines (Detailed Execution Loops)
    # Pipeline A: Mascot Video Broadcast
    d.add(Rect(12, 95, 115, 75, fillColor=colors.HexColor("#FFF7ED"), strokeColor=colors.HexColor("#F97316"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(69, 158, "VIDEO BROADCAST", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#C2410C")))
    d.add(String(69, 146, "15 FPS Mascot Card", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#EA580C")))
    d.add(String(69, 134, "SafeVideoMediaBuffer", fontSize=6.5, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(69, 122, "RGB24 -> VideoSocket.Send()", fontSize=6, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(69, 111, "Prevents Sleep & Gallery Drop", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#059669")))
    d.add(String(69, 102, "Stage Gallery Promoted", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Pipeline B: Two-Way Audio & Whisper STT
    d.add(Rect(137, 95, 115, 75, fillColor=colors.HexColor("#FEF2F2"), strokeColor=colors.HexColor("#EF4444"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(194, 158, "AUDIO & WHISPER STT", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#B91C1C")))
    d.add(String(194, 146, "OnAudioMediaReceived", fontSize=6.5, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(194, 134, "AudioAggregator (16kHz PCM)", fontSize=6, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#B91C1C")))
    d.add(String(194, 122, "Rolling Chunks (min 0.5s)", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(194, 111, "RealtimeSpeechRecognizer", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(194, 102, "Live Transcript Stream", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Pipeline C: 1080p Screen Share (VBSS)
    d.add(Rect(262, 95, 115, 75, fillColor=colors.HexColor("#F0FDF4"), strokeColor=colors.HexColor("#22C55E"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(319, 158, "VBSS SCREEN RECORDER", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#15803D")))
    d.add(String(319, 146, "OnVbssMediaReceived", fontSize=6.5, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(319, 134, "1080p (1920x1080) NV12", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#16A34A")))
    d.add(String(319, 122, "MjpegAviWriter / Keyframes", fontSize=6, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(319, 111, "Active Presenter Subscription", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(319, 102, "ScreenShare_1080p.avi", fontSize=6.5, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Pipeline D: In-Meeting Interactive Chat / TTS
    d.add(Rect(387, 95, 105, 75, fillColor=colors.HexColor("#FAF5FF"), strokeColor=colors.HexColor("#A855F7"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(439, 158, "INTERACTIVE TTS & CHAT", fontSize=7, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#7E22CE")))
    d.add(String(439, 146, "HandleIncomingChatActivity", fontSize=6, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#4B5563")))
    d.add(String(439, 134, "• 'status' / 'recording'", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#6B7280")))
    d.add(String(439, 122, "• 'kaise ho' / 'help'", fontSize=6, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#6B7280")))
    d.add(String(439, 111, "AudioSender.SpeakAsync()", fontSize=6, fontName="Courier-Bold", textAnchor="middle", fillColor=colors.HexColor("#7E22CE")))
    d.add(String(439, 102, "Voice + Rich HTML Replies", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#059669")))

    # Feed down lines from Media Engine (Step 3) to Pipelines
    d.add(Line(360, 185, 69, 170, strokeColor=colors.HexColor("#10B981"), strokeWidth=1))
    d.add(Line(69, 170, 69, 170))
    d.add(Line(400, 185, 194, 170, strokeColor=colors.HexColor("#10B981"), strokeWidth=1))
    d.add(Line(440, 185, 319, 170, strokeColor=colors.HexColor("#10B981"), strokeWidth=1))
    d.add(Line(470, 185, 439, 170, strokeColor=colors.HexColor("#10B981"), strokeWidth=1))

    # Row 3: Call Termination, Synthesis & Artifact Generation
    d.add(Rect(12, 12, 480, 68, fillColor=colors.HexColor("#F8FAFC"), strokeColor=colors.HexColor("#64748B"), strokeWidth=1.5, rx=5, ry=5))
    d.add(String(252, 66, "4. CALL TERMINATION, MoM COMPILATION & CLOUD STORAGE", fontSize=8, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#0F172A")))
    d.add(String(252, 54, "Trigger: HandleCallEndedAsync() • StopVbssRecorder() • Finalize AVIs • Audio Flush", fontSize=7, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#334155")))
    
    # Sub-boxes for artifacts
    d.add(Rect(22, 20, 105, 26, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#CBD5E1"), strokeWidth=1, rx=3, ry=3))
    d.add(String(74, 34, "Audio WAV Files", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#0284C7")))
    d.add(String(74, 25, "Speaker_X.wav / Mix.wav", fontSize=5.5, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#64748B")))

    d.add(Rect(137, 20, 105, 26, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#CBD5E1"), strokeWidth=1, rx=3, ry=3))
    d.add(String(189, 34, "1080p Video Files", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#16A34A")))
    d.add(String(189, 25, "ScreenShare_1080p.avi", fontSize=5.5, fontName="Courier", textAnchor="middle", fillColor=colors.HexColor("#64748B")))

    d.add(Rect(252, 20, 115, 26, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#CBD5E1"), strokeWidth=1, rx=3, ry=3))
    d.add(String(309, 34, "Transcripts (JSON/TXT)", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#2563EB")))
    d.add(String(309, 25, "Speaker Diarized Segments", fontSize=5.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#64748B")))

    d.add(Rect(377, 20, 105, 26, fillColor=colors.HexColor("#FFFFFF"), strokeColor=colors.HexColor("#CBD5E1"), strokeWidth=1, rx=3, ry=3))
    d.add(String(429, 34, "Word MoM (.docx)", fontSize=6.5, fontName="Helvetica-Bold", textAnchor="middle", fillColor=colors.HexColor("#9333EA")))
    d.add(String(429, 25, "DocxWriter + Cloud GCS", fontSize=5.5, fontName="Helvetica", textAnchor="middle", fillColor=colors.HexColor("#64748B")))

    # Pipeline to Termination connectors
    d.add(Line(69, 95, 74, 46, strokeColor=colors.HexColor("#94A3B8"), strokeWidth=1, strokeDashArray=[2, 2]))
    d.add(Line(194, 95, 189, 46, strokeColor=colors.HexColor("#94A3B8"), strokeWidth=1, strokeDashArray=[2, 2]))
    d.add(Line(319, 95, 309, 46, strokeColor=colors.HexColor("#94A3B8"), strokeWidth=1, strokeDashArray=[2, 2]))
    d.add(Line(439, 95, 429, 46, strokeColor=colors.HexColor("#94A3B8"), strokeWidth=1, strokeDashArray=[2, 2]))

    return d


def generate_pdf(output_pdf_path, mascot_image_path):
    doc = SimpleDocTemplate(
        output_pdf_path,
        pagesize=letter,
        leftMargin=54,
        rightMargin=54,
        topMargin=54,
        bottomMargin=54
    )

    styles = getSampleStyleSheet()
    
    # Custom Typography Styles
    title_style = ParagraphStyle(
        'DocTitle',
        parent=styles['Heading1'],
        fontName='Helvetica-Bold',
        fontSize=20,
        leading=24,
        textColor=colors.HexColor("#1E1B4B")
    )
    subtitle_style = ParagraphStyle(
        'DocSubTitle',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=10,
        leading=14,
        textColor=colors.HexColor("#4B5563")
    )
    h1_style = ParagraphStyle(
        'Heading1_Custom',
        parent=styles['Heading1'],
        fontName='Helvetica-Bold',
        fontSize=13,
        leading=17,
        textColor=colors.HexColor("#1E1B4B"),
        spaceBefore=12,
        spaceAfter=6
    )
    h2_style = ParagraphStyle(
        'Heading2_Custom',
        parent=styles['Heading2'],
        fontName='Helvetica-Bold',
        fontSize=10,
        leading=14,
        textColor=colors.HexColor("#2563EB"),
        spaceBefore=8,
        spaceAfter=4
    )
    body_style = ParagraphStyle(
        'Body_Custom',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=8.5,
        leading=12,
        textColor=colors.HexColor("#1F2937")
    )
    bullet_style = ParagraphStyle(
        'Bullet_Custom',
        parent=styles['Normal'],
        fontName='Helvetica',
        fontSize=8,
        leading=11,
        textColor=colors.HexColor("#374151"),
        leftIndent=12
    )

    story = []

    # ==========================================
    # PAGE 1: TITLE, EXECUTIVE SUMMARY & HIGH-LEVEL DIAGRAM
    # ==========================================
    story.append(Paragraph("Microsoft Teams Calling Bot", title_style))
    story.append(Paragraph("End-to-End System Architecture: High-Level Topology & Low-Level Media Pipeline", subtitle_style))
    story.append(Spacer(1, 8))

    # Metadata & Badges Table
    meta_data = [
        [
            Paragraph("<b>Target System:</b> Teams Calling Bot (TSL AI / TDA)", body_style),
            Paragraph("<b>Platform:</b> Graph Calling SDK + App-Hosted Media", body_style)
        ],
        [
            Paragraph("<b>Protocol Stack:</b> HTTPS (443), UDP (8413 SRTP), net.tcp", body_style),
            Paragraph("<b>Media Engines:</b> 15 FPS Video, 16kHz PCM Audio, 1080p VBSS", body_style)
        ],
        [
            Paragraph("<b>Invocation:</b> Zero-Link Chat (#joincall) + Power Automate", body_style),
            Paragraph("<b>Status:</b> Production Ready & Verified Live", body_style)
        ]
    ]
    meta_table = Table(meta_data, colWidths=[250, 254])
    meta_table.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, -1), colors.HexColor("#F1F5F9")),
        ('BOX', (0, 0), (-1, -1), 1, colors.HexColor("#CBD5E1")),
        ('INNERGRID', (0, 0), (-1, -1), 0.5, colors.HexColor("#E2E8F0")),
        ('TOPPADDING', (0, 0), (-1, -1), 4),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 4),
        ('LEFTPADDING', (0, 0), (-1, -1), 8),
        ('RIGHTPADDING', (0, 0), (-1, -1), 8),
    ]))
    story.append(meta_table)
    story.append(Spacer(1, 10))

    # Section 1: High-Level System Architecture
    story.append(Paragraph("1. High-Level System Architecture (Ingress, Core & Media Platform)", h1_style))
    story.append(Paragraph(
        "The architecture decouples signaling and media processing. Inbound invocation occurs through either "
        "<b>Zero-Link in-chat auto-extraction</b> (<code>#joincall</code> via Bot Framework Messaging) or "
        "<b>Power Automate calendar forwarding</b>. The bot orchestrates call state with Microsoft Graph, while the "
        "app-hosted C++ Media Engine (MediaPlatform) handles real-time SRTP audio, video, and screen share data.",
        body_style
    ))
    story.append(Spacer(1, 6))

    # Add High-Level Diagram
    story.append(create_high_level_diagram())
    story.append(Spacer(1, 8))

    # Key Architectural Principles Bullet Grid
    arch_notes = [
        "• <b>Zero-Link Architecture:</b> The bot extracts the meeting thread ID, tenant ID, and caller AAD Object ID directly from Teams chat activity payloads. No manual meeting URL parsing is required.",
        "• <b>Power Automate & Calendar Forwarding:</b> Forwarded meeting emails to the TSL AI mailbox automatically register the meeting in the persistent registry, triggering auto-join at the scheduled start time.",
        "• <b>App-Hosted Media Engine:</b> Custom media platform runs natively on Windows, receiving raw 16kHz PCM audio packets and emitting a continuous 15 FPS 720p status card video stream to maintain Teams stage gallery presence."
    ]
    for note in arch_notes:
        story.append(Paragraph(note, bullet_style))

    story.append(PageBreak())

    # ==========================================
    # PAGE 2: LOW-LEVEL PIPELINE & CALL LIFECYCLE
    # ==========================================
    story.append(Paragraph("2. Low-Level Component Interaction & Execution Pipeline", h1_style))
    story.append(Paragraph(
        "This low-level specification illustrates the precise sequence of execution from activity ingestion through "
        "Graph SDK call establishment, local media buffer binding, real-time Whisper transcription, 1080p screen share capture, "
        "and automated post-call Minutes of Meeting (.docx) generation.",
        body_style
    ))
    story.append(Spacer(1, 6))

    # Add Low-Level Diagram
    story.append(create_low_level_diagram())
    story.append(Spacer(1, 8))

    story.append(Paragraph("3. Technical Protocol & Media Specifications", h1_style))
    
    spec_headers = [
        Paragraph("<b>Subsystem</b>", body_style),
        Paragraph("<b>Format / Standard</b>", body_style),
        Paragraph("<b>Buffer & Rate Spec</b>", body_style),
        Paragraph("<b>Operational Purpose</b>", body_style)
    ]
    spec_rows = [
        spec_headers,
        [
            Paragraph("Signaling Ingress", body_style),
            Paragraph("HTTPS / JSON REST", body_style),
            Paragraph("Port 443 (DuckDNS / TLS)", body_style),
            Paragraph("Receives /api/messages & /api/calling/notify", body_style)
        ],
        [
            Paragraph("Media Platform", body_style),
            Paragraph("SRTP / UDP", body_style),
            Paragraph("Port 8413 (App-Hosted)", body_style),
            Paragraph("Direct peer-to-peer media stream with Teams edge", body_style)
        ],
        [
            Paragraph("Mascot Video", body_style),
            Paragraph("RGB24 Uncompressed", body_style),
            Paragraph("1280x720 @ 15 FPS", body_style),
            Paragraph("Maintains active video stage presence in gallery", body_style)
        ],
        [
            Paragraph("Audio Ingestion", body_style),
            Paragraph("Linear PCM 16-bit", body_style),
            Paragraph("16,000 Hz Mono (640 bytes/chunk)", body_style),
            Paragraph("Recorded to WAV & fed to Whisper STT", body_style)
        ],
        [
            Paragraph("Screen Share (VBSS)", body_style),
            Paragraph("NV12 / MJPEG AVI", body_style),
            Paragraph("1920x1080 Full HD @ 30 FPS", body_style),
            Paragraph("Subscribed when #video flag is present", body_style)
        ],
        [
            Paragraph("Meeting MoM", body_style),
            Paragraph("OpenXML (.docx)", body_style),
            Paragraph("Custom DocxWriter Engine", body_style),
            Paragraph("Summarizes transcripts, tasks & uploads to cloud", body_style)
        ]
    ]
    spec_table = Table(spec_rows, colWidths=[100, 110, 130, 164])
    spec_table.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), colors.HexColor("#1E1B4B")),
        ('TEXTCOLOR', (0, 0), (-1, 0), colors.white),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 3),
        ('TOPPADDING', (0, 0), (-1, -1), 3),
        ('LEFTPADDING', (0, 0), (-1, -1), 5),
        ('RIGHTPADDING', (0, 0), (-1, -1), 5),
        ('BOX', (0, 0), (-1, -1), 1, colors.HexColor("#94A3B8")),
        ('INNERGRID', (0, 0), (-1, -1), 0.5, colors.HexColor("#CBD5E1")),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor("#F8FAFC")]),
    ]))
    story.append(spec_table)
    story.append(Spacer(1, 8))

    story.append(PageBreak())

    # ==========================================
    # PAGE 3: COMMAND REFERENCE & DEPLOYMENT TOPOLOGY
    # ==========================================
    story.append(Paragraph("4. Manual & Automated Command Reference", h1_style))
    story.append(Paragraph(
        "The following matrix outlines all commands supported by the bot across in-meeting chat, automated calendar forwarding, "
        "and direct REST management APIs:",
        body_style
    ))
    story.append(Spacer(1, 6))

    cmd_headers = [
        Paragraph("<b>Command / Channel</b>", body_style),
        Paragraph("<b>Syntax / Payload</b>", body_style),
        Paragraph("<b>Action & Features Initialized</b>", body_style)
    ]
    cmd_rows = [
        cmd_headers,
        [
            Paragraph("<b>Zero-Link Join</b><br/>(In-Meeting Chat)", body_style),
            Paragraph("<code>#joincall</code><br/><code>!join</code><br/><code>/join</code>", body_style),
            Paragraph("Auto-extracts meeting thread from chat. Immediately joins with 15 FPS Mascot Video, 16kHz PCM Audio, Real-Time Whisper STT, Greeting Chime, and Word MoM.", body_style)
        ],
        [
            Paragraph("<b>Join + 1080p Video</b><br/>(In-Meeting Chat)", body_style),
            Paragraph("<code>#joincall #video</code><br/><code>!join --video</code><br/><code>#joincall -v</code>", body_style),
            Paragraph("Activates all features above <b>PLUS records presenter 1080p screen share (VBSS)</b> into timestamped AVI/MP4 files.", body_style)
        ],
        [
            Paragraph("<b>Interactive Bot</b><br/>(In-Meeting Chat)", body_style),
            Paragraph("<code>status</code><br/><code>help</code><br/><code>kaise ho</code>", body_style),
            Paragraph("Bot responds in real-time in chat and audibly speaks via TTS: reports audio/video recording health and greets participants.", body_style)
        ],
        [
            Paragraph("<b>Power Automate</b><br/>(Calendar Forward)", body_style),
            Paragraph("<code>POST /api/powerautomate/<br/>register-meeting</code>", body_style),
            Paragraph("Registers scheduled meetings or raw forwarded invite emails. Background scheduler auto-joins at meeting start time.", body_style)
        ],
        [
            Paragraph("<b>Immediate API Join</b><br/>(Backend / Web)", body_style),
            Paragraph("<code>POST /joinCall</code><br/><code>{\"JoinUrl\": \"...\"}</code>", body_style),
            Paragraph("Direct backend API call forcing the bot to connect to an explicit Teams meetup URL.", body_style)
        ],
        [
            Paragraph("<b>Leave Call</b><br/>(Management)", body_style),
            Paragraph("<code>POST /api/management/<br/>calls/{id}/leave</code>", body_style),
            Paragraph("Gracefully hangs up call, flushes all audio buffers, finalizes video containers, and compiles the final Word MoM.", body_style)
        ]
    ]
    cmd_table = Table(cmd_rows, colWidths=[120, 140, 244])
    cmd_table.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), colors.HexColor("#1E1B4B")),
        ('TEXTCOLOR', (0, 0), (-1, 0), colors.white),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 4),
        ('TOPPADDING', (0, 0), (-1, -1), 4),
        ('LEFTPADDING', (0, 0), (-1, -1), 5),
        ('RIGHTPADDING', (0, 0), (-1, -1), 5),
        ('BOX', (0, 0), (-1, -1), 1, colors.HexColor("#94A3B8")),
        ('INNERGRID', (0, 0), (-1, -1), 0.5, colors.HexColor("#CBD5E1")),
        ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor("#F8FAFC")]),
    ]))
    story.append(cmd_table)
    story.append(Spacer(1, 10))

    # Live Mascot Card Image (Verification Evidence)
    if os.path.exists(mascot_image_path):
        story.append(Paragraph("5. Live Broadcast Verification Telemetry", h1_style))
        story.append(Paragraph(
            "Below is the verified live 15 FPS Mascot Broadcast Card streamed by the bot over <code>net.tcp</code> "
            "and SRTP into the Microsoft Teams stage gallery during live calls:",
            body_style
        ))
        story.append(Spacer(1, 4))
        story.append(Image(mascot_image_path, width=4.8 * inch, height=2.7 * inch))
        story.append(Spacer(1, 4))
        story.append(Paragraph("<i>Figure: Live 15 FPS Video Stream Captured on Active Teams Meeting Stage Gallery</i>", ParagraphStyle('Caption', parent=styles['Normal'], fontSize=7.5, fontName='Helvetica-Oblique', textColor=colors.HexColor('#6B7280'), alignment=1)))

    # Build the document
    doc.build(story, canvasmaker=NumberedCanvas)
    print(f"PDF successfully generated at: {output_pdf_path}")

if __name__ == "__main__":
    base_dir = r"c:\Users\jaidevlalgame\Downloads\TeamsCallingBot\TeamsCallingBot\TeamsCallingBot"
    mascot_img = r"C:\Users\jaidevlalgame\.gemini\antigravity-ide\brain\f7426c11-f2b4-4321-b705-cc2ba3f62f2c\bot_full_mascot_card.jpg"
    out_pdf = os.path.join(base_dir, "TEAMS_CALLING_BOT_ARCHITECTURE_HIGH_AND_LOW_LEVEL.pdf")
    generate_pdf(out_pdf, mascot_img)
