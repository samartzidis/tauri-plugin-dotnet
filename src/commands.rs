use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::{command, Runtime, State, Webview};

use crate::{error::BridgeError, DotNet};

/// The request as .NET's `BridgeRequest` expects it.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CallRequest<'a> {
  call_id: &'a str,
  method: &'a str,
  args: &'a [Value],
}

/// The response as .NET's `BridgeResponse` produces it. A call without a return value has neither field.
#[derive(Deserialize)]
struct CallResponse {
  #[serde(default)]
  result: Option<Value>,
  #[serde(default)]
  error: Option<BridgeError>,
}

fn parse_response(raw: &str) -> Result<Value, BridgeError> {
  let response: CallResponse = serde_json::from_str(raw).map_err(|e| {
    BridgeError::new(
      "HostProtocolError",
      format!("Invalid response from the .NET host: {e}"),
    )
  })?;

  match response.error {
    Some(error) => Err(error),
    None => Ok(response.result.unwrap_or(Value::Null)),
  }
}

/// `invoke("plugin:dotnet|call", { callId, method, args })`
///
/// Resolves with the .NET return value; rejects with a [`BridgeError`] when the call fails.
#[command]
pub(crate) async fn call<R: Runtime>(
  webview: Webview<R>,
  state: State<'_, DotNet>,
  call_id: String,
  method: String,
  args: Vec<Value>,
) -> Result<Value, BridgeError> {
  let request = serde_json::to_string(&CallRequest {
    call_id: &call_id,
    method: &method,
    args: &args,
  })
  .map_err(|e| BridgeError::new("HostProtocolError", format!("Failed to encode the request: {e}")))?;

  let (tx, rx) = tokio::sync::oneshot::channel();
  state.host.call(
    webview.label(),
    request,
    Box::new(move |response| {
      // The receiver is gone if the frontend abandoned the call; nothing to do then.
      let _ = tx.send(response);
    }),
  );

  let raw = rx.await.map_err(|_| {
    BridgeError::new(
      "HostStopped",
      "The .NET host stopped before completing the call.",
    )
  })?;

  parse_response(&raw)
}

/// `invoke("plugin:dotnet|cancel", { callId })`
#[command]
pub(crate) async fn cancel(state: State<'_, DotNet>, call_id: String) -> Result<(), BridgeError> {
  state.host.cancel(&call_id);
  Ok(())
}

#[cfg(test)]
mod tests {
  use super::*;
  use crate::host::{DotNetHost, NoHost};
  use serde_json::json;
  use std::sync::{Arc, Mutex};

  #[test]
  fn parses_a_result() {
    let value = parse_response(r#"{"callId":"c1","result":{"firstName":"Ada"}}"#).unwrap();
    assert_eq!(value, json!({ "firstName": "Ada" }));
  }

  #[test]
  fn a_call_without_a_return_value_resolves_to_null() {
    assert_eq!(parse_response(r#"{"callId":"c1"}"#).unwrap(), Value::Null);
    assert_eq!(parse_response(r#"{"callId":"c1","result":null}"#).unwrap(), Value::Null);
  }

  #[test]
  fn falsy_results_are_preserved() {
    assert_eq!(parse_response(r#"{"result":0}"#).unwrap(), json!(0));
    assert_eq!(parse_response(r#"{"result":false}"#).unwrap(), json!(false));
    assert_eq!(parse_response(r#"{"result":""}"#).unwrap(), json!(""));
  }

  #[test]
  fn parses_an_error() {
    let error =
      parse_response(r#"{"callId":"c1","error":{"message":"boom","type":"InvalidOperationException"}}"#)
        .unwrap_err();
    assert_eq!(error, BridgeError::new("InvalidOperationException", "boom"));
  }

  #[test]
  fn garbage_from_the_host_is_a_protocol_error() {
    let error = parse_response("not json").unwrap_err();
    assert_eq!(error.kind, "HostProtocolError");
  }

  #[test]
  fn errors_serialize_with_a_type_field() {
    let json = serde_json::to_value(BridgeError::new("Boom", "it broke")).unwrap();
    assert_eq!(json, json!({ "message": "it broke", "type": "Boom" }));
  }

  #[test]
  fn requests_serialize_in_the_shape_dotnet_expects() {
    let args = [json!("World"), json!(2)];
    let request = serde_json::to_value(CallRequest {
      call_id: "c1",
      method: "GreetService.Greet",
      args: &args,
    })
    .unwrap();
    assert_eq!(
      request,
      json!({ "callId": "c1", "method": "GreetService.Greet", "args": ["World", 2] })
    );
  }

  #[test]
  fn the_placeholder_host_fails_calls_instead_of_hanging() {
    let response = Arc::new(Mutex::new(None));
    let slot = response.clone();
    NoHost.call("main", "{}".into(), Box::new(move |r| *slot.lock().unwrap() = Some(r)));

    let raw = response.lock().unwrap().take().expect("completion must be invoked");
    assert_eq!(parse_response(&raw).unwrap_err().kind, "HostNotConfigured");
  }
}
