use wuji_rebuild_agent::command_server::client::PipeClient;

fn main() {
    let channel = std::env::args().nth(1).expect("channel");
    let sid = wuji_windows::current_user_sid().expect("sid");
    let scope = wuji_core::runtime_names::user_scope(&sid);
    let pipe = format!("\\\\.\\pipe\\WUJI.Rebuild.V01.Test.{channel}.{scope}");
    let mut client = PipeClient::connect(&pipe).expect("connect");
    println!("hello => ok={}", client.hello(&channel)["ok"]);

    let big = "x".repeat(70 * 1024);
    println!("sending oversize ({}KiB)...", big.len() / 1024);
    let response = client.call(
        &ulid::Ulid::generate().to_string(),
        "status_get",
        serde_json::json!({ "blob": big }),
    );
    println!("oversize => {response}");
    println!("done");
}
