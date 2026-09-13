using System.Net;
using System.Text;

namespace DropScan.Sharing;

public static class ReceiverPage
{
    static string E(string text) => WebUtility.HtmlEncode(text);
    public static string Render(ShareSession session)
    {
        var rows = new StringBuilder();
        foreach (var file in session.Files)
            if (file.IsDirectory) rows.Append($"<li><div><strong>{E(file.ArchivePath)}</strong><small>โฟลเดอร์ว่าง • รับผ่าน ZIP</small></div></li>");
            else rows.Append($"<li><div><strong>{E(file.ArchivePath)}</strong><small>{E(SelectedFile.FormatSize(file.Length))}</small></div><a href='/s/{session.Token}/f/{file.Id}' download>ดาวน์โหลด</a></li>");
        string accessNote = session.ApprovalRequired ? "ผู้ส่งอนุมัติคำขอนี้แล้ว" : session.PinEnabled ? "รายการนี้ยืนยัน PIN แล้ว" : "ลิงก์นี้ให้สิทธิ์เข้าถึงไฟล์";
        return Shell("รับไฟล์จาก DropScan", $"<span class='tag'>พร้อมรับไฟล์</span><h1>ไฟล์ที่ส่งถึงคุณ</h1><p>{session.Files.Count(f => !f.IsDirectory)} ไฟล์ • {E(SelectedFile.FormatSize(session.Files.Sum(f => f.Length)))}</p><a class='zip' href='/s/{session.Token}/all.zip' download>ดาวน์โหลดทั้งหมดเป็น ZIP</a><ul>{rows}</ul><p class='note'>หมดอายุ {E(session.ExpiresAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm zzz"))}<br>ไฟล์เดี่ยวรองรับดาวน์โหลดต่อผ่านเบราว์เซอร์ที่รองรับ • ZIP ต้องเริ่มใหม่หากขาดการเชื่อมต่อ</p><footer>ให้ผู้ส่งเปิดโปรแกรมและเครื่องไว้ระหว่างรับไฟล์<br>เชื่อมต่อเครือข่ายเดียวกัน • {accessNote}</footer>");
    }
    public static string PinPrompt(string token, string? message = null)
    {
        string feedback = string.IsNullOrEmpty(message) ? "ขอ PIN 6 หลักจากผู้ส่ง แล้วกรอกด้านล่าง" : message;
        return Shell("ยืนยัน PIN", $"<span class='tag'>ป้องกันด้วย PIN</span><h1>กรอก PIN เพื่อรับไฟล์</h1><p>{E(feedback)}</p><form method='post' action='/s/{E(token)}/pin'><label for='pin'>PIN 6 หลัก</label><input id='pin' name='pin' type='password' inputmode='numeric' pattern='[0-9]{{6}}' maxlength='6' autocomplete='one-time-code' required autofocus><button type='submit'>ยืนยัน PIN</button></form><p class='note'>PIN ไม่ได้อยู่ใน QR • ติดต่อผู้ส่งหากยังไม่ได้รับ PIN</p><footer>ไฟล์ส่งผ่าน HTTP ภายในเครือข่ายเดียวกัน<br>PIN ใช้ควบคุมการเข้าถึง แต่ไม่ได้เข้ารหัสข้อมูลระหว่างทาง</footer>");
    }
    public static string ApprovalPrompt(string token) => Shell("ขออนุมัติรับไฟล์", $"<span class='tag'>ต้องได้รับอนุมัติ</span><h1>ส่งคำขอไปยังผู้ส่ง</h1><p>ผู้ส่งจะเห็น IP ของอุปกรณ์นี้ และเลือกอนุมัติหรือปฏิเสธคำขอ</p><form method='post' action='/s/{E(token)}/request'><button type='submit' autofocus>ส่งคำขอรับไฟล์</button></form><p class='note'>คำขอมีอายุ 5 นาที • รายชื่อไฟล์จะแสดงหลังผู้ส่งอนุมัติ</p><footer>เปิดหน้านี้ค้างไว้ระหว่างรอผู้ส่ง<br>ไฟล์ต้นฉบับยังอยู่ที่เครื่องผู้ส่ง</footer>");
    public static string ApprovalWaiting(string token) => Shell("กำลังรอผู้ส่งอนุมัติ", $"<span class='tag waiting'>รออนุมัติ</span><h1>ส่งคำขอแล้ว</h1><p>กำลังรอผู้ส่งอนุมัติ หน้านี้จะตรวจสถานะให้อัตโนมัติ</p><a class='secondary' href='/s/{E(token)}'>ตรวจสถานะตอนนี้</a><form method='post' action='/s/{E(token)}/request/cancel'><button class='secondary' type='submit'>ยกเลิกคำขอ</button></form><p class='note'>คำขอหมดอายุภายใน 5 นาที และยังไม่เปิดเผยรายชื่อไฟล์</p><footer>ให้ผู้ส่งเปิด DropScan เพื่อจัดการคำขอ</footer>", "<meta http-equiv='refresh' content='3'>");
    public static string ApprovalEnded(string token, string title, string message) => Shell(title, $"<span class='tag'>คำขอสิ้นสุด</span><h1>{E(title)}</h1><p>{E(message)}</p><form method='post' action='/s/{E(token)}/request'><button type='submit' autofocus>ส่งคำขอใหม่</button></form><footer>คำขอใหม่ต้องได้รับการอนุมัติจากผู้ส่งอีกครั้ง</footer>");
    public static string Error(string title, string message) => Shell(title, $"<span class='tag'>DropScan</span><h1>{E(title)}</h1><p>{E(message)}</p><footer>ไฟล์ต้นฉบับอยู่ที่เครื่องผู้ส่ง</footer>");
    static string Shell(string title, string body, string extraHead = "") => "<!doctype html><html lang='th'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" + extraHead + "<title>" + E(title) + "</title><style>" + Css + "</style></head><body><header><b>↗ DropScan</b><span>LOCAL TRANSFER</span></header><main>" + body + "</main></body></html>";
    const string Css = "*{box-sizing:border-box}body{margin:0;background:#f5f6f8;color:#182230;font:16px/1.6 'Segoe UI',Tahoma,sans-serif;padding:28px 18px}header{max-width:680px;margin:0 auto 26px;display:flex;align-items:center;justify-content:space-between;gap:12px}header b{font-size:24px}header span{font-size:10px;color:#667085;letter-spacing:1px}main{max-width:680px;margin:auto;background:white;border:1px solid #e4e7ec;border-radius:16px;padding:28px}.tag{color:#2563eb;background:#e8efff;padding:5px 10px;border-radius:6px;font-size:12px}.waiting{color:#8a4b0f;background:#fff4df}h1{font-size:28px;margin:18px 0 8px}p{color:#667085}ul{list-style:none;padding:0;margin:24px 0}li{display:flex;align-items:center;gap:16px;padding:18px 0;border-bottom:1px solid #edf0f5}li div{min-width:0;flex:1}strong{display:block;overflow-wrap:anywhere;font-weight:600}small{color:#667085}a,button{display:inline-block;background:#2563eb;color:white;text-decoration:none;border:1px solid #2563eb;border-radius:8px;padding:10px 15px;white-space:nowrap;font:inherit;cursor:pointer}.secondary{background:white;color:#344054;border-color:#b8c1ce}.zip{margin-top:12px}a:hover,button:hover{background:#1d4ed8;color:white}.secondary:hover{background:#f3f5f8;color:#182230}a:focus-visible,button:focus-visible,input:focus-visible{outline:3px solid #93b4ff;outline-offset:3px}form{max-width:360px;margin:24px 0;display:grid;gap:8px}label{font-weight:600}input{border:1px solid #b8c1ce;border-radius:8px;padding:11px 12px;font:20px 'Segoe UI',Tahoma,sans-serif;letter-spacing:5px}form button{margin-top:8px}.note,footer{font-size:13px;color:#667085}footer{margin-top:26px;padding-top:18px;border-top:1px solid #edf0f5}@media(max-width:420px){main{padding:20px}h1{font-size:24px}li{align-items:flex-start;flex-direction:column}a{width:100%;text-align:center}}";
}


