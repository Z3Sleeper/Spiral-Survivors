using System.Collections;
using TMPro;
using UnityEngine;

namespace PurrLobby
{
    public class CodeButton : MonoBehaviour
    {
        [SerializeField] private TMP_Text codeText;

        private string _roomId;
        private Coroutine _clickEffectCoroutine;
        
        public void Init(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return;
            _roomId = roomId;
            codeText.text = _roomId;
        }

        public void CopyCode()
        {
            GUIUtility.systemCopyBuffer = _roomId;

            if(_clickEffectCoroutine != null)
                StopCoroutine(_clickEffectCoroutine);
            _clickEffectCoroutine = StartCoroutine(ClickEffect(_roomId));
        }

        private WaitForSeconds _waitToReturn = new (1f);
        private IEnumerator ClickEffect(string roomId)
        {
            codeText.text = "Copied!";
            yield return _waitToReturn;
            codeText.text = roomId;
        }
    }
}
