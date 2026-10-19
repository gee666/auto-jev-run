"""pip install typesafe-sdk; set TYPESAFE_BASE_URL and TYPESAFE_API_KEY to switch providers."""
import os
from typesafe_sdk import TypeSafeClient, Noul

with TypeSafeClient(
    base_url=os.environ.get('TYPESAFE_BASE_URL', 'http://localhost:8765'),
    api_key=os.environ['TYPESAFE_API_KEY'],
    timeout=1200,
) as client:
    result = client.system_one(
        state='Python: a = [1, 2]; b = a; b.append(3)',
        questions={'shared': Noul(instructions='Does a now contain three elements?')},
    )
    print(result.nouls['shared'].noul)
