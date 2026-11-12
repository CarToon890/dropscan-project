# DropScan — Local Transfer

โปรแกรม Windows สำหรับส่งไฟล์โดยตรงให้ผู้รับใน Wi-Fi/LAN เดียวกัน ผู้รับสแกน QR และดาวน์โหลดผ่านเบราว์เซอร์

## เปิดใช้งาน

เปิด `bin/Release/net8.0-windows/DropScan.exe` โดยเก็บไฟล์อื่นในโฟลเดอร์เดียวกันไว้ด้วย รุ่นนี้ต้องมี .NET 8 Desktop Runtime และ ASP.NET Core Runtime บนเครื่อง (เครื่องพัฒนาปัจจุบันมีแล้ว)

1. เลือกไฟล์หรือโฟลเดอร์ (ลากวางได้) ตรวจรายการ แล้วกดถัดไป
2. เลือก IP ของ Wi-Fi/Ethernet หรือ Cloudflare Quick Tunnel (ทดลอง), อายุรายการ และเลือกเปิด PIN 6 หลักหรือการอนุมัติผู้รับได้ตามต้องการ
3. กดเริ่มแชร์ ให้ผู้รับสแกน QR หรือเปิดลิงก์ หากเปิด PIN ให้ส่ง PIN แยกจาก QR หากเปิดการอนุมัติ ให้จัดการคำขอจากหน้ารายการแชร์
4. ให้โปรแกรมและเครื่องเปิดอยู่ระหว่างรับไฟล์

ตัวเลือก 127.0.0.1 ใช้ทดสอบบนเครื่องผู้ส่งเท่านั้น ถ้ามือถือเข้าถึงไม่ได้ ให้ตรวจ IP, guest Wi-Fi isolation และสิทธิ์ Windows Firewall เฉพาะเครือข่ายส่วนตัว โปรแกรมไม่แก้ Firewall อัตโนมัติ

## พฤติกรรม

- แชร์ครั้งละหนึ่งรายการ ดาวน์โหลดได้พร้อมกันสูงสุด 3 คำขอ
- คัดลอกลิงก์, ดูสถานะ/เปอร์เซ็นต์/ความเร็ว, ยกเลิกการส่งปัจจุบัน, หยุดแชร์ทั้งหมด และหมดอายุจริง
- `ส่งข้อมูลครบแล้ว` หมายถึงเซิร์ฟเวอร์ส่ง response ครบ ไม่ใช่การยืนยันว่าเบราว์เซอร์บันทึกไฟล์ลงดิสก์แล้ว
- หยุดแชร์/หมดอายุยกเลิกการส่งที่ยังทำงาน ไบต์ที่ผู้รับได้รับไปแล้วไม่สามารถเรียกคืนได้
- ใช้ token สุ่ม 256 บิต ตรวจสิทธิ์ทุกเส้นทาง ไฟล์ต้นฉบับไม่ถูกลบหรือแก้ไข
- เปิด PIN 6 หลักได้เป็นรายรายการ ผู้รับต้องยืนยันก่อนเห็นชื่อไฟล์หรือดาวน์โหลดไฟล์รายตัว, ZIP และ Range
- PIN ผิดครบ 5 ครั้งจากเครื่องเดิมจะพักการลอง 60 วินาที QR และลิงก์ไม่มี PIN ฝังอยู่
- หลังยืนยันสำเร็จ เบราว์เซอร์เก็บสิทธิ์สุ่มในคุกกี้ HttpOnly จนรายการหมดอายุหรือผู้ส่งหยุดแชร์
- เปิดการอนุมัติผู้รับเป็นรายรายการได้ ผู้รับต้องส่งคำขอ ผู้ส่งจะเห็น IP และอนุมัติหรือปฏิเสธได้ คำขอที่ยังไม่ตัดสินใจหมดอายุใน 5 นาที
- ผู้รับยกเลิกหรือส่งคำขอใหม่ได้ สิทธิ์ที่อนุมัติผูกกับคุกกี้สุ่มของเบราว์เซอร์นั้น และทุกเส้นทางไฟล์/ZIP/HEAD/Range ตรวจสิทธิ์เดียวกัน
- คำขอและ IP ผู้รับอยู่ในหน่วยความจำเฉพาะรอบแชร์ ไม่บันทึกลงประวัติ และคำขอที่รอพร้อมกันถูกจำกัดไว้ 32 รายการ
- PIN และสิทธิ์รับไฟล์ไม่ถูกบันทึกในประวัติ โหมด LAN ใช้ HTTP จึงไม่เข้ารหัสข้อมูลระหว่างทาง ควรใช้เฉพาะเครือข่ายที่ไว้ใจได้และหลีกเลี่ยงไฟล์อ่อนไหว
- เปลี่ยน IP, ปิดเครื่อง หรือปิดโปรแกรมแล้วลิงก์เดิมไม่รับประกันว่าใช้ได้ ต้องสร้างรายการใหม่
- ดาวน์โหลดทั้งหมดเป็น ZIP แบบสตรีม เก็บโครงสร้างและโฟลเดอร์ว่าง ไม่สร้าง ZIP สำเนาถาวรในเครื่องผู้ส่ง และข้าม symbolic link/junction
- ไฟล์รายตัวรองรับ HTTP Range / If-Range สำหรับไคลเอนต์ที่ดาวน์โหลดต่อได้; ZIP ต้องเริ่มรับใหม่เมื่อขาดการเชื่อมต่อ
- ความคืบหน้า ZIP นับไบต์ต้นฉบับที่อ่านเข้าการบีบอัด ไม่ใช่ขนาด ZIP ที่บีบอัดแล้ว
- ย่อหน้าต่างหรือกด “ย่อไป Tray” เพื่อแชร์ต่อ เปิดกลับด้วยการดับเบิลคลิกไอคอนหรือเมนู “เปิด DropScan”; เมนู “หยุดแชร์และออก” จะปิดโปรแกรม
- แจ้งเตือนเมื่อส่ง response ครบ การแสดงแจ้งเตือนขึ้นอยู่กับการตั้งค่าของ Windows
- ประวัติสรุปสูงสุด 200 รายการอยู่ที่ `%LocalAppData%\DropScan\history.json` ไม่เก็บ token ชื่อไฟล์ หรือ path ต้นฉบับ และไม่เปิดแชร์เก่าอัตโนมัติ
- Cloudflare Quick Tunnel เป็นการทดสอบ: ต้องติดตั้ง `cloudflared`, URL สาธารณะเปลี่ยนเมื่อเริ่มใหม่ และไฟล์รับส่งผ่าน Cloudflare; Cloudflare ระบุว่าไม่มี uptime guarantee และ Quick Tunnel มีไว้สำหรับทดสอบ/พัฒนา
- HTTPS ที่ผู้รับเห็นมาจาก Cloudflare; การเชื่อมต่อจาก cloudflared ถึงแอปในเครื่องเป็น HTTP ผ่าน loopback เครื่องผู้ส่ง

## Build และทดสอบ

```powershell
dotnet restore --configfile NuGet.Config
dotnet build -c Release --no-restore

dotnet restore tests/TransferSmoke/TransferSmoke.csproj --configfile NuGet.Config
dotnet build tests/TransferSmoke/TransferSmoke.csproj -c Release --no-restore -o artifacts/network-tests
dotnet artifacts/network-tests/TransferSmoke.dll

dotnet restore tests/DesktopSmoke/DesktopSmoke.csproj --configfile NuGet.Config
dotnet build tests/DesktopSmoke/DesktopSmoke.csproj -c Release --no-restore -o artifacts/ui-tests
dotnet artifacts/ui-tests/DesktopSmoke.dll artifacts/local-ui-qa
```

ทดสอบเครือข่ายบน loopback จริง และตรวจ SHA-256, ไฟล์ว่าง, ลิงก์ผิด, การยกเลิก, disconnect, หมดอายุ, concurrency และไฟล์เปลี่ยน ทดสอบ UI อ่าน QR กลับด้วย ZXing และสร้างภาพ WPF ที่ 125%/150%; ไม่ทดแทนการตรวจด้วยมือถือจริง

ทดสอบระยะ 2 (ZIP, โฟลเดอร์, Range และประวัติ):

```powershell
powershell -ExecutionPolicy Bypass -File tests/PhaseTwoSmoke/run.ps1
```

สคริปต์สร้าง junction สำหรับตรวจว่าแอปปฏิเสธลิงก์โฟลเดอร์ แล้วรันทดสอบ HTTP จริง 34 จุด ใช้ไฟล์ทดสอบใน artifacts/phase2-qa

ทดสอบ PIN ระยะ 3:

```powershell
powershell -ExecutionPolicy Bypass -File tests/PhaseThreeSmoke/run.ps1
```

ชุดนี้ทดสอบ HTTP จริง 30 จุด ครอบคลุม PIN ผิด/ถูก, การพัก 60 วินาที, คุกกี้สิทธิ์, การซ่อนข้อมูลก่อนยืนยัน และสิทธิ์ของไฟล์รายตัว, ZIP, HEAD และ Range

ทดสอบการอนุมัติผู้รับระยะ 3:

```powershell
powershell -ExecutionPolicy Bypass -File tests/ApprovalSmoke/run.ps1
```

ชุดนี้ทดสอบ HTTP จริง 37 จุด ครอบคลุมการส่งคำขอ อนุมัติ ปฏิเสธ ยกเลิก หมดอายุ เพดานคำขอ การผูกสิทธิ์กับเบราว์เซอร์ การซ่อนข้อมูล และการใช้ร่วมกับ PIN

ดูรายละเอียดงานต่อและเกณฑ์ตรวจรับใน [WORK_PLAN.md](WORK_PLAN.md)

## สร้างแพ็กเกจ Windows แบบ Self-contained

ใช้ Windows x64 ที่ติดตั้ง .NET 8 SDK แล้วรัน:

```powershell
powershell -ExecutionPolicy Bypass -File tools/Publish-Windows.ps1
```

สคริปต์สร้างไฟล์ ZIP ที่ `artifacts/packages/DropScan-win-x64.zip` และโฟลเดอร์ publish ที่ `artifacts/publish/win-x64` โดยแพ็ก .NET Runtime ไปกับโปรแกรมแล้ว ผู้รับไฟล์ในโหมด LAN ไม่ต้องติดตั้งอะไรเพิ่ม; โหมด Cloudflare Quick Tunnel ยังต้องติดตั้ง `cloudflared` แยก

คู่มือสำหรับผู้ใช้ปลายทางจะถูกรวมไว้ใน ZIP ชื่อ `README-user.md` ดูข้อจำกัดและขั้นตอนเปิดแอปได้จากไฟล์นั้น

การทดสอบ Cloudflare Quick Tunnel:

1. ติดตั้ง `cloudflared` สำหรับ Windows จาก [เอกสาร Cloudflare](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/)
2. ปิดแล้วเปิด DropScan ใหม่ให้ค้นพบ `cloudflared` ใน PATH
3. ในขั้นเตรียมรายการ เลือก “ข้ามเครือข่าย — Cloudflare Quick Tunnel (ทดลอง)” แล้วเริ่มแชร์
4. ตรวจว่า URL และ QR เป็น HTTPS จากนั้นทดสอบเปิด/ดาวน์โหลดผ่านเครือข่ายมือถือ

ผู้ถือ URL สาธารณะสามารถเข้าถึงรายการได้ตามตัวเลือก PIN/อนุมัติผู้รับ และ Cloudflare เป็นผู้ให้บริการระหว่างทาง จึงไม่ควรใช้กับไฟล์อ่อนไหว

## อ้างอิงการพัฒนา

- [Microsoft: Kestrel endpoint configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0)
- [QRCoder](https://www.nuget.org/packages/QRCoder/1.8.0) สำหรับ QR ในโปรแกรม
- [ZXing.Net](https://www.nuget.org/packages/ZXing.Net/0.16.11) ใช้เฉพาะชุดทดสอบอ่าน QR กลับ


